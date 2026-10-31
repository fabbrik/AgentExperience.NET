using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>One Claude Code session's link to the run it is working in.</summary>
internal sealed record HookSession
{
    public required string SessionId { get; init; }

    /// <summary>The open run the session's tool calls go to; <see langword="null"/> until a prompt opens one.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// The session's holdout arm, drawn once from its ID: every run of the session is given an ID in this arm, so the
    /// session, not the turn, is the unit the holdout randomizes. <see langword="null"/> while the holdout is off.
    /// </summary>
    public HoldoutArm? Arm { get; set; }

    /// <summary>How many runs the session has opened: the run IDs are derived from it.</summary>
    public int Turns { get; set; }

    /// <summary>The open run has a call that changed something (not only reading), so Stop verifies it.</summary>
    public bool RunChanged { get; set; }

    /// <summary>
    /// No run ID in the session's arm was found within the bound: the open run gets no block and no hint, and nothing is
    /// recorded to the holdout for it (fail closed). Practically unreachable at sane rates.
    /// </summary>
    public bool RunUnpinned { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The Claude Code sessions of a project, one owner-only file each under <c>.aexp/sessions/</c>, beside the config. Hooks
/// run as separate processes, possibly at once (a turn's parallel tool calls), so every read-modify-write holds the
/// session's lock file, exactly as run journals do; a hook that also changes the run takes the session lock first, then
/// the run's.
/// </summary>
internal sealed class HookSessionStore(string directory)
{
    public string Directory { get; } = directory;

    /// <summary>
    /// Whether <paramref name="sessionId"/> can name a file: Claude Code's session IDs are UUIDs; anything with a
    /// character outside letters, digits, '-', '_' and '.' (or a dot-only name) is refused rather than mapped.
    /// </summary>
    public static bool IsValidId(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId)
        && sessionId.Length <= 128
        && sessionId.Trim('.').Length > 0
        && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private string PathOf(string sessionId) => Path.Combine(Directory, sessionId + ".json");

    private string LockOf(string sessionId) => Path.Combine(Directory, sessionId + ".lock");

    /// <summary>Reads the session (a new one when it has no file), lets <paramref name="update"/> change it, and writes it back, under its lock.</summary>
    public async Task<T> UpdateAsync<T>(string sessionId, DateTimeOffset now, Func<HookSession, Task<T>> update, CancellationToken cancellationToken)
    {
        EnsureDirectory();
        await using var held = await FileLock.AcquireAsync(LockOf(sessionId), "The Claude Code session", cancellationToken).ConfigureAwait(false);
        var session = Read(sessionId) ?? new HookSession { SessionId = sessionId };
        try
        {
            return await update(session).ConfigureAwait(false);
        }
        finally
        {
            // Written even when the update failed part way: a run it opened stays linked to the session.
            session.UpdatedAt = now;
            AtomicFile.Write(PathOf(sessionId), JsonSerializer.SerializeToUtf8Bytes(session, RunJournal.Json), overwrite: true);
        }
    }

    /// <summary>
    /// Deletes session files (with their lock files) not updated since <paramref name="cutoff"/> that no hook is holding,
    /// and stray lock files as old. Returns how many sessions were removed.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(id) || File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime)
            {
                continue;
            }

            FileStream held;
            try
            {
                held = FileLock.Open(LockOf(id));
            }
            catch (IOException ex) when (FileLock.IsSharingViolation(ex))
            {
                continue;
            }

            await using (held.ConfigureAwait(false))
            {
                File.Delete(path);
                removed++;
            }

            TryDelete(LockOf(id));
        }

        foreach (var lockFile in System.IO.Directory.EnumerateFiles(Directory, "*.lock"))
        {
            if (!File.Exists(Path.ChangeExtension(lockFile, ".json")) && File.GetLastWriteTimeUtc(lockFile) < cutoff.UtcDateTime)
            {
                TryDelete(lockFile);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return removed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Removes the session's file once the session ended (its lock file with it).</summary>
    public void Delete(string sessionId)
    {
        foreach (var path in new[] { PathOf(sessionId), LockOf(sessionId) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private HookSession? Read(string sessionId)
    {
        try
        {
            return File.Exists(PathOf(sessionId))
                ? JsonSerializer.Deserialize<HookSession>(File.ReadAllBytes(PathOf(sessionId)), RunJournal.Json)
                : null;
        }
        catch (JsonException)
        {
            // A damaged file is replaced by a fresh session: the open run, if any, is pruned like any abandoned one.
            return null;
        }
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>
/// Pins a Claude Code session to one holdout arm. The arm is the library's own assignment
/// (<see cref="HoldoutAssignmentFunction"/>, with the configured salt and rate) of a GUID derived from the session ID;
/// each run's ID is then drawn from a deterministic sequence (session, turn, attempt) until the library assigns it the
/// same arm, so the block and every hint of every run in the session follow the session's arm.
/// </summary>
internal static class SessionHoldout
{
    /// <summary>The most candidate IDs tried for one run before failing closed.</summary>
    public const int MaxAttempts = 4096;

    public static HoldoutArm ArmOf(string sessionId, string salt, double rate) =>
        HoldoutAssignmentFunction.Assign(DerivedGuid("aexp:claude-code:session\n" + sessionId), salt, rate);

    /// <summary>The library's arm for a run ID: what injection and recall will do for that run.</summary>
    public static HoldoutArm ArmOfRun(Guid runId, string salt, double rate) => HoldoutAssignmentFunction.Assign(runId, salt, rate);

    /// <summary>A run ID in <paramref name="arm"/> for the session's turn; <see langword="null"/> when the bound is hit.</summary>
    public static Guid? RunIdFor(string sessionId, int turn, HoldoutArm arm, string salt, double rate)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = DerivedGuid($"aexp:claude-code:run\n{sessionId}\n{turn}\n{attempt}");
            if (HoldoutAssignmentFunction.Assign(candidate, salt, rate) == arm)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>A version 8 (custom) GUID from the SHA-256 of <paramref name="text"/>.</summary>
    internal static Guid DerivedGuid(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>
/// The hooks' log, <c>.aexp/hook.log</c>: one line per hook, owner-only, bounded (past <see cref="MaxBytes"/> it is
/// moved to <c>hook.log.1</c>, replacing the previous one). It holds what aexp did and its own messages, never what the
/// hook input carried (no prompt, no tool input or output), so nothing from the session's content lands in it.
/// </summary>
internal sealed class HookLog(string path, TimeProvider clock)
{
    public const long MaxBytes = 256 * 1024;

    public string Path { get; } = path;

    public void Write(string hookEvent, string? sessionId, string message)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            var info = new FileInfo(Path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(Path, Path + ".1", overwrite: true);
            }

            var session = sessionId is null ? "-" : sessionId.Length > 13 ? sessionId[..13] : sessionId;
            var line = $"{clock.GetUtcNow():yyyy-MM-dd'T'HH:mm:ss'Z'} {hookEvent} session={session} {OneLine(message)}\n";
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(Path, options);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is best effort: a hook never fails because it could not write it.
        }
    }

    private static string OneLine(string message)
    {
        var line = new string([.. message.Select(c => char.IsControl(c) ? ' ' : c)]);
        return line.Length > 1000 ? line[..1000] : line;
    }
}
