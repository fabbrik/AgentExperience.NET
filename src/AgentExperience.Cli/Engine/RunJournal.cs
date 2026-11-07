using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;

namespace AgentExperience.Cli.Engine;

/// <summary>One captured tool call, already sanitized by the library's capture rules before it was written down.</summary>
internal sealed record JournalCall(
    Guid ToolCallId,
    string ToolName,
    JsonObject Arguments,
    DateTimeOffset StartedAt,
    string? Result,
    string? Error,
    string? RecipeExcerpt);

/// <summary>
/// A run between <c>aexp run start</c> and <c>aexp run end</c>, kept in one owner-only file per run beside the config, so
/// separate processes can add to it. The library's run store holds completed runs only, so an open run lives here until
/// <c>run end</c> hands it to capture, which completes it and writes it to the run store; the file is then deleted.
/// Every call passes through the configured capture rules, and the task text through the same text rules as a tool
/// result, before anything is written.
/// </summary>
internal sealed record RunJournal
{
    public required Guid RunId { get; init; }

    public required string TaskId { get; init; }

    public required string Task { get; init; }

    /// <summary>The scope the run was opened under; a later command under another config's scope refuses it.</summary>
    public required Scope Scope { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public List<JournalCall> Calls { get; init; } = [];

    /// <summary>The records the library delivered into this run (the block's and the hints'), as capture records exposure.</summary>
    public List<RunExposure> Exposures { get; init; } = [];

    /// <summary>The block the run was given, once it asked; asking again prints it again instead of delivering it twice.</summary>
    public string? Block { get; set; }

    public bool BlockRequested { get; set; }

    /// <summary>The failure signatures already answered with a hint (or withheld), so each is answered once per run.</summary>
    public List<string> AnsweredSignatures { get; init; } = [];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>
/// Reads and writes run journals. Each run has a lock file beside it, held exclusively for a read-modify-write, so two
/// processes never interleave one run's writes; the journal itself is replaced whole (written to a temporary file, then
/// renamed over it), so a cancelled or crashed command leaves the previous version or the new one, never half of one.
/// </summary>
internal sealed class RunJournalStore(string directory)
{
    /// <summary>
    /// How long a command waits for another process holding the same run. A hang guard, not a schedule: a holder may be
    /// asking the library for a block or a hint, which reads the store.
    /// </summary>
    internal static TimeSpan LockWait
    {
        get => FileLock.Wait;
        set => FileLock.Wait = value;
    }

    public string Directory { get; } = directory;

    public string PathOf(Guid runId) => Path.Combine(Directory, runId.ToString("D") + ".json");

    private string LockOf(Guid runId) => Path.Combine(Directory, runId.ToString("D") + ".lock");

    public bool Exists(Guid runId) => File.Exists(PathOf(runId));

    public Task CreateAsync(RunJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();
        Configuration.AtomicFile.Write(PathOf(journal.RunId), JsonSerializer.SerializeToUtf8Bytes(journal, RunJournal.Json), overwrite: false);
        return Task.CompletedTask;
    }

    /// <summary>Reads a run without its lock, for a look that changes nothing.</summary>
    public RunJournal? Peek(Guid runId)
    {
        try
        {
            return JsonSerializer.Deserialize<RunJournal>(File.ReadAllBytes(PathOf(runId)), RunJournal.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the run, lets <paramref name="update"/> change it, and writes it back, all under the run's lock. Once
    /// <paramref name="update"/> has returned, the write is not cancelled: what it did is recorded.
    /// </summary>
    public async Task<T> UpdateAsync<T>(Guid runId, Func<RunJournal, Task<T>> update, CancellationToken cancellationToken)
    {
        if (!Exists(runId))
        {
            throw new AexpUsageException($"No open run {runId:D}: start one with 'aexp run start', or it has already ended.");
        }

        await using var held = await FileLock.AcquireAsync(LockOf(runId), $"Run {runId:D}", cancellationToken).ConfigureAwait(false);
        var journal = Peek(runId)
            ?? throw new AexpUsageException($"No open run {runId:D}: it has already ended, or its file cannot be read.");
        var result = await update(journal).ConfigureAwait(false);
        Configuration.AtomicFile.Write(PathOf(runId), JsonSerializer.SerializeToUtf8Bytes(journal, RunJournal.Json), overwrite: true);
        return result;
    }

    /// <summary>Deletes the run's file once capture holds the completed run (the caller holds no lock on it).</summary>
    public void Delete(Guid runId)
    {
        foreach (var path in new[] { PathOf(runId), LockOf(runId) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover journal is refused by the next 'run end' (capture finds the run already stored); prune removes it.
            }
        }
    }

    /// <summary>
    /// Deletes open runs started before <paramref name="cutoff"/> (by their recorded start, else the file's time) that no
    /// process is holding, with their lock files. Returns how many were removed.
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
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var runId))
            {
                continue;
            }

            var started = Peek(runId)?.StartedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (started >= cutoff)
            {
                continue;
            }

            FileStream? held;
            try
            {
                held = FileLock.Open(LockOf(runId));
            }
            catch (IOException ex) when (FileLock.IsSharingViolation(ex))
            {
                continue; // In use right now: not stale.
            }

            await using (held.ConfigureAwait(false))
            {
                File.Delete(path);
                removed++;
            }

            try
            {
                File.Delete(LockOf(runId));
            }
            catch (IOException)
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return removed;
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
/// An exclusive lock between aexp processes: a lock file opened with <see cref="FileShare.None"/>, held for one
/// read-modify-write. Run journals and Claude Code session files use it.
/// </summary>
internal static class FileLock
{
    /// <summary>
    /// How long a command waits for another process holding the same lock. A hang guard, not a schedule: a holder may be
    /// asking the library for a block or a hint, which reads the store.
    /// </summary>
    internal static TimeSpan Wait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Takes the lock at <paramref name="path"/>, waiting while another process holds it.</summary>
    public static async Task<FileStream> AcquireAsync(string path, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            try
            {
                return Open(path);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                // Another aexp process holds it; wait for it. Any other I/O failure is not waited out.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new AexpUsageException($"{what} stayed busy with another aexp command for {Wait}; try again.");
            }
        }
    }

    public static FileStream Open(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>
    /// The lock is held elsewhere: ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION on Windows; on Unix, where .NET takes
    /// an advisory lock for <see cref="FileShare.None"/>, the would-block errno it reports (EAGAIN: 11 on Linux, 35 on
    /// macOS and the BSDs). Anything else (a missing directory, a permission) is not waited out.
    /// </summary>
    internal static bool IsSharingViolation(IOException exception) =>
        exception.GetType() == typeof(IOException)
        && (OperatingSystem.IsWindows()
            ? (exception.HResult & 0xFFFF) is 32 or 33
            : exception.HResult == (OperatingSystem.IsLinux() ? 11 : 35));
}

/// <summary>
/// A request the tool cannot carry out as asked: reported to the caller, never a crash. <see cref="Exception.Message"/>
/// is written by this tool and safe to show a model; <see cref="Detail"/> (a path, a parser's or the operating system's
/// words) goes to the operator's standard error only.
/// </summary>
internal class AexpUsageException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>The verifier did not finish within its bound: nothing was finalized, and the run can be finalized again.</summary>
internal sealed class AexpVerifierTimedOutException(string message) : AexpUsageException(message);
