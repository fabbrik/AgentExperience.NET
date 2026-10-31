using System.Diagnostics;
using AgentExperience.Cli.Configuration;

namespace AgentExperience.Cli.Engine;

/// <summary>The verifier's answer: its exit code, or that it did not finish in time.</summary>
internal sealed record VerifierResult(int ExitCode, bool TimedOut);

/// <summary>
/// Runs the configured verifier: the one command this tool ever executes. It is started directly from its argument vector
/// (no shell, so nothing in it is interpreted), in the configured working directory, with its standard input closed and
/// its output read and discarded (it may print secrets, and under <c>aexp mcp</c> standard output is the protocol). A
/// verifier still running at the timeout is killed with its process tree.
/// </summary>
internal static class VerifierRunner
{
    public static async Task<VerifierResult> RunAsync(VerifierSetting verifier, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(verifier.Command[0])
        {
            WorkingDirectory = verifier.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in verifier.Command.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new AexpUsageException($"The verifier '{verifier.Command[0]}' could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AexpUsageException($"The verifier '{verifier.Command[0]}' could not be started: {ex.Message}");
        }

        process.StandardInput.Close();
        var drainOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(verifier.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended on its own in the meantime.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new VerifierResult(-1, TimedOut: true);
        }

        // A process the verifier left behind may hold its output open; the verdict does not wait for it.
        await Task.WhenAny(Task.WhenAll(drainOutput, drainError), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        return new VerifierResult(process.ExitCode, TimedOut: false);
    }
}
