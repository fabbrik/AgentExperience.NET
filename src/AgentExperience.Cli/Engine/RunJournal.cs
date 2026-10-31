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
/// Only sanitized content is ever written: every call passes through the configured capture rules first.
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

/// <summary>Reads and writes run journals under an exclusive lock, so two processes never interleave one run's writes.</summary>
internal sealed class RunJournalStore(string directory)
{
    /// <summary>How long a command waits for another process holding the same run; a hang guard, not a schedule.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    public string Directory { get; } = directory;

    public string PathOf(Guid runId) => Path.Combine(Directory, runId.ToString("D") + ".json");

    public async Task CreateAsync(RunJournal journal, CancellationToken cancellationToken)
    {
        EnsureDirectory();
        var path = PathOf(journal.RunId);
        await using var stream = Open(path, FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, journal, RunJournal.Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the run, lets <paramref name="update"/> change it, and writes it back, all under the run's lock.</summary>
    public async Task<T> UpdateAsync<T>(Guid runId, Func<RunJournal, Task<T>> update, CancellationToken cancellationToken)
    {
        var path = PathOf(runId);
        if (!File.Exists(path))
        {
            throw new AexpUsageException($"No open run {runId:D}: start one with 'aexp run start', or it has already ended.");
        }

        await using var stream = await OpenLockedAsync(path, cancellationToken).ConfigureAwait(false);
        var journal = await JsonSerializer.DeserializeAsync<RunJournal>(stream, RunJournal.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new AexpUsageException($"The run file for {runId:D} is empty.");
        var result = await update(journal).ConfigureAwait(false);
        stream.SetLength(0);
        stream.Position = 0;
        await JsonSerializer.SerializeAsync(stream, journal, RunJournal.Json, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Deletes the run's file once capture holds the completed run.</summary>
    public void Delete(Guid runId)
    {
        try
        {
            File.Delete(PathOf(runId));
        }
        catch (IOException)
        {
            // A leftover file is refused by the next 'run end' (capture finds the run already stored).
        }
    }

    private static FileStream Open(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows() && mode == FileMode.CreateNew)
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static async Task<FileStream> OpenLockedAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return Open(path, FileMode.Open);
            }
            catch (FileNotFoundException)
            {
                throw new AexpUsageException($"No open run in '{path}': it has already ended.");
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Another aexp process holds this run; wait for it.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
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

/// <summary>A request the tool cannot carry out as asked: reported to the caller, never a crash.</summary>
internal sealed class AexpUsageException(string message) : Exception(message);
