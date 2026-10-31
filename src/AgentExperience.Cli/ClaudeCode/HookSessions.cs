using System.Text.Json;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>One Claude Code session's link to the run it is working in.</summary>
internal sealed record HookSession
{
    public required string SessionId { get; init; }

    /// <summary>The open run the session's tool calls go to; <see langword="null"/> until a prompt opens one.</summary>
    public Guid? RunId { get; set; }

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
        var result = await update(session).ConfigureAwait(false);
        session.UpdatedAt = now;
        AtomicFile.Write(PathOf(sessionId), JsonSerializer.SerializeToUtf8Bytes(session, RunJournal.Json), overwrite: true);
        return result;
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
