using System.Diagnostics;

namespace AgentExperience.CiTriage.GitHub;

/// <summary>What a child process printed and how it exited.</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs <c>gh</c> and <c>git</c> with an argument list (never a shell string), capturing both streams.</summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // gh and git need neither secret: keep the model key and the database connection out of their environment.
        start.Environment.Remove("OPENROUTER_API_KEY");
        start.Environment.Remove("AGENTEXPERIENCE_CITRIAGE_PG");
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
