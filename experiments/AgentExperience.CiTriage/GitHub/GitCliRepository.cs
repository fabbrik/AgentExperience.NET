namespace AgentExperience.CiTriage.GitHub;

/// <summary>
/// The local clone, read through <c>git</c>. A commit that is not in the clone (a force-pushed branch, a pull request's
/// head) is fetched from <c>origin</c> by its id once; when that fails it is unavailable.
/// </summary>
internal sealed class GitCliRepository(string root) : IRepository
{
    private readonly Dictionary<string, bool> _available = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>?> ChangedFilesAsync(string sha, CancellationToken cancellationToken)
    {
        if (!await EnsureAsync(sha, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Against the first parent, so a merge commit lists everything it brought in. A root commit has no parent.
        var result = await GitAsync(["diff", "--name-only", sha + "^1", sha], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? Lines(result.StandardOutput) : [];
    }

    public async Task<IReadOnlyList<string>?> DiffNamesAsync(string fromSha, string toSha, CancellationToken cancellationToken)
    {
        if (!await EnsureAsync(fromSha, cancellationToken).ConfigureAwait(false) || !await EnsureAsync(toSha, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = await GitAsync(["diff", "--name-only", fromSha, toSha], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? Lines(result.StandardOutput) : null;
    }

    public async Task<string?> ShowFileAsync(string sha, string path, CancellationToken cancellationToken)
    {
        if (!await EnsureAsync(sha, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = await GitAsync(["show", sha + ":" + path], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private async Task<bool> EnsureAsync(string sha, CancellationToken cancellationToken)
    {
        if (_available.TryGetValue(sha, out var known))
        {
            return known;
        }

        var present = (await GitAsync(["cat-file", "-e", sha + "^{commit}"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
        if (!present)
        {
            _ = await GitAsync(["fetch", "--quiet", "origin", sha], cancellationToken).ConfigureAwait(false);
            present = (await GitAsync(["cat-file", "-e", sha + "^{commit}"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
        }

        _available[sha] = present;
        return present;
    }

    private Task<ProcessResult> GitAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync("git", arguments, root, cancellationToken);

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
