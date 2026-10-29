using AgentExperience.CiTriage.GitHub;

namespace AgentExperience.CiTriage.Scripted;

/// <summary>
/// Fake CI history for <c>--scripted</c> and the tests: runs, jobs, logs and commits held in memory. Nothing here touches
/// the network or a git clone.
/// </summary>
public sealed class FakeCi : IGitHub, IRepository
{
    private readonly List<CiRun> _runs = [];
    private readonly Dictionary<long, List<CiJob>> _jobs = [];
    private readonly Dictionary<long, string?> _logs = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), IReadOnlyList<string>> _diffs = [];
    private readonly Dictionary<(string, string), string> _files = [];
    private long _nextId = 1000;

    /// <summary>How many calls reached this fake; there is no other source, so a scripted pass makes no network call.</summary>
    public int Calls { get; private set; }

    /// <summary>
    /// A failed run with one failed job carrying <paramref name="log"/> (<see langword="null"/>: GitHub no longer has it),
    /// and the commit's changed files; with <paramref name="greenSha"/>, a later green run on the same branch and the
    /// fix files between the two commits.
    /// </summary>
    public CiRun AddFailure(
        string branch,
        string failSha,
        DateTimeOffset failedAt,
        IReadOnlyList<string> changedFiles,
        string? log,
        string? greenSha = null,
        DateTimeOffset? greenAt = null,
        IReadOnlyList<string>? fixFiles = null)
    {
        var failure = AddRun(branch, failSha, "failure", failedAt);
        var job = new CiJob(_nextId++, "build-and-test", "failure");
        _jobs[failure.Id] = [new CiJob(_nextId++, "python (3.13)", "success"), job];
        _logs[job.Id] = log;
        _changed[failSha] = changedFiles;
        foreach (var file in changedFiles)
        {
            _files[(failSha, file)] = "// " + file + " at " + failSha + "\n";
        }

        if (greenSha is not null)
        {
            AddRun(branch, greenSha, "success", greenAt ?? failedAt.AddHours(1));
            _diffs[(failSha, greenSha)] = fixFiles ?? [];
        }

        return failure;
    }

    /// <summary>A completed run with no jobs of interest (a green run elsewhere, say).</summary>
    public CiRun AddRun(string branch, string sha, string conclusion, DateTimeOffset completedAt)
    {
        var run = new CiRun(_nextId++, branch, sha, conclusion, completedAt.AddMinutes(-10), completedAt, 1);
        _runs.Add(run);
        _jobs.TryAdd(run.Id, []);
        return run;
    }

    public Task<IReadOnlyList<CiRun>> ListCompletedRunsAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<CiRun>>([.. _runs]);
    }

    public Task<IReadOnlyList<CiJob>> ListJobsAsync(long runId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<CiJob>>(_jobs.TryGetValue(runId, out var jobs) ? [.. jobs] : []);
    }

    public Task<string?> GetJobLogAsync(long jobId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_logs.GetValueOrDefault(jobId));
    }

    public Task<IReadOnlyList<string>?> ChangedFilesAsync(string sha, CancellationToken cancellationToken) =>
        Task.FromResult(_changed.TryGetValue(sha, out var files) ? files : null);

    public Task<IReadOnlyList<string>?> DiffNamesAsync(string fromSha, string toSha, CancellationToken cancellationToken) =>
        Task.FromResult(_diffs.TryGetValue((fromSha, toSha), out var files) ? files : null);

    public Task<string?> ShowFileAsync(string sha, string path, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryGetValue((sha, path), out var text) ? text : null);
}
