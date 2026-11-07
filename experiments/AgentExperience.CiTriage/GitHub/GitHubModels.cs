namespace AgentExperience.CiTriage.GitHub;

/// <summary>One completed run of the <c>CI</c> workflow, at its final attempt (the runs API reports the latest attempt).</summary>
/// <param name="Id">The run id.</param>
/// <param name="Branch">The head branch.</param>
/// <param name="HeadSha">The commit the run tested.</param>
/// <param name="Conclusion">The final attempt's conclusion: <c>success</c>, <c>failure</c>, <c>cancelled</c>, ...</param>
/// <param name="CreatedAt">When the run was created.</param>
/// <param name="CompletedAt">When its final attempt finished (the runs API's <c>updated_at</c>).</param>
/// <param name="Attempt">The final attempt's number.</param>
public sealed record CiRun(long Id, string Branch, string HeadSha, string Conclusion, DateTimeOffset CreatedAt, DateTimeOffset CompletedAt, int Attempt)
{
    public bool Succeeded => string.Equals(Conclusion, "success", StringComparison.Ordinal);

    public bool Failed => string.Equals(Conclusion, "failure", StringComparison.Ordinal);
}

/// <summary>One job of a run.</summary>
public sealed record CiJob(long Id, string Name, string? Conclusion)
{
    public bool Failed => string.Equals(Conclusion, "failure", StringComparison.Ordinal);
}

/// <summary>The GitHub data the tool reads. <see cref="GhCliGitHub"/> reads it through the <c>gh</c> CLI; tests and <c>--scripted</c> use fakes.</summary>
public interface IGitHub
{
    /// <summary>Every completed run of the CI workflow, any order.</summary>
    Task<IReadOnlyList<CiRun>> ListCompletedRunsAsync(CancellationToken cancellationToken);

    /// <summary>The jobs of one run.</summary>
    Task<IReadOnlyList<CiJob>> ListJobsAsync(long runId, CancellationToken cancellationToken);

    /// <summary>One job's log, or <see langword="null"/> when GitHub no longer has it (404/410).</summary>
    Task<string?> GetJobLogAsync(long jobId, CancellationToken cancellationToken);
}

/// <summary>Read-only access to the repository's commits. <see cref="GitCliRepository"/> runs <c>git</c>; tests use a fake.</summary>
public interface IRepository
{
    /// <summary>The files the commit changed against its first parent, or <see langword="null"/> when the commit is unavailable.</summary>
    Task<IReadOnlyList<string>?> ChangedFilesAsync(string sha, CancellationToken cancellationToken);

    /// <summary><c>git diff --name-only from to</c>, or <see langword="null"/> when either commit is unavailable.</summary>
    Task<IReadOnlyList<string>?> DiffNamesAsync(string fromSha, string toSha, CancellationToken cancellationToken);

    /// <summary><c>git show sha:path</c>, or <see langword="null"/> when the path does not exist at that commit.</summary>
    Task<string?> ShowFileAsync(string sha, string path, CancellationToken cancellationToken);
}
