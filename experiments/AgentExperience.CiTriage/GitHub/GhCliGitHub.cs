using System.Globalization;

namespace AgentExperience.CiTriage.GitHub;

/// <summary>
/// The real GitHub data, through <c>gh api</c> (the user's own <c>gh</c> login; the tool never sees a token). Runs come
/// from <c>repos/{o}/{r}/actions/workflows/ci.yml/runs</c>, jobs from <c>actions/runs/{id}/jobs</c> and logs from
/// <c>actions/jobs/{id}/logs</c>; <c>gh run view --log-failed</c> is not used, since it comes back empty for some runs.
/// Logs are cached under <paramref name="cacheDirectory"/> (gitignored), and a log GitHub no longer has is remembered too.
/// </summary>
internal sealed class GhCliGitHub(string repository, string cacheDirectory) : IGitHub
{
    public async Task<IReadOnlyList<CiRun>> ListCompletedRunsAsync(CancellationToken cancellationToken)
    {
        var lines = await ApiLinesAsync(
            $"repos/{repository}/actions/workflows/ci.yml/runs?status=completed&per_page=100",
            ".workflow_runs[] | [.id, .head_branch, .head_sha, (.conclusion // \"\"), .created_at, .updated_at, .run_attempt] | @tsv",
            cancellationToken).ConfigureAwait(false);

        return [.. lines.Select(line => line.Split('\t')).Where(fields => fields.Length == 7).Select(fields => new CiRun(
            long.Parse(fields[0], CultureInfo.InvariantCulture),
            fields[1],
            fields[2],
            fields[3],
            DateTimeOffset.Parse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            DateTimeOffset.Parse(fields[5], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            int.Parse(fields[6], CultureInfo.InvariantCulture)))];
    }

    public async Task<IReadOnlyList<CiJob>> ListJobsAsync(long runId, CancellationToken cancellationToken)
    {
        var lines = await ApiLinesAsync(
            $"repos/{repository}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/jobs?per_page=100",
            ".jobs[] | [.id, .name, (.conclusion // \"\")] | @tsv",
            cancellationToken).ConfigureAwait(false);

        return [.. lines.Select(line => line.Split('\t')).Where(fields => fields.Length == 3).Select(fields => new CiJob(
            long.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2]))];
    }

    public async Task<string?> GetJobLogAsync(long jobId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(cacheDirectory, "logs");
        Directory.CreateDirectory(directory);
        var id = jobId.ToString(CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, id + ".log");
        var missing = Path.Combine(directory, id + ".missing");
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(missing))
        {
            return null;
        }

        var result = await ProcessRunner.RunAsync("gh", ["api", $"repos/{repository}/actions/jobs/{id}/logs"], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("HTTP 404", StringComparison.Ordinal) || result.StandardError.Contains("HTTP 410", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(missing, string.Empty, cancellationToken).ConfigureAwait(false);
                return null;
            }

            throw new InvalidOperationException($"gh api failed reading the log of job {id} (exit {result.ExitCode}).");
        }

        await File.WriteAllTextAsync(path, result.StandardOutput, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private static async Task<IReadOnlyList<string>> ApiLinesAsync(string endpoint, string jq, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("gh", ["api", "--paginate", endpoint, "--jq", jq], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh api {endpoint.Split('?')[0]} failed (exit {result.ExitCode}). Is gh installed and logged in?");
        }

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
