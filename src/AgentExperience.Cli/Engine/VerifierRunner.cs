using System.Diagnostics;
using AgentExperience.Cli.Configuration;

namespace AgentExperience.Cli.Engine;

/// <summary>The verifier's answer: its exit code, or that it did not finish in time.</summary>
internal sealed record VerifierResult(int ExitCode, bool TimedOut);

/// <summary>
/// Runs the configured verifier: the one command this tool ever executes. Its program is resolved here, never by a shell
/// or by the operating system's search (which may look in the current directory): a program named with a directory
/// separator is resolved against the configured working directory and must lie inside the project root (unless it is
/// absolute); a bare name is looked up on <c>PATH</c> only, skipping empty and relative entries. It is started from its
/// absolute path and argument vector, with its standard input closed and its output read and discarded (it may print
/// secrets, and under <c>aexp mcp</c> standard output is the protocol). A verifier still running at the timeout is killed
/// with its whole process tree.
/// </summary>
internal static class VerifierRunner
{
    /// <summary>The absolute path of the verifier's program, by the rules above.</summary>
    public static string ResolveProgram(VerifierSetting verifier, string projectRoot, Func<string, string?> environment)
    {
        var program = verifier.Command[0];
        if (Path.IsPathFullyQualified(program))
        {
            return IsExecutable(program) ? program : throw new AexpUsageException("The verifier program does not exist or is not executable.", program);
        }

        if (program.Contains('/') || program.Contains('\\'))
        {
            var resolved = Path.GetFullPath(program, verifier.WorkingDirectory);
            if (!AexpConfigLoader.IsInside(projectRoot, resolved))
            {
                throw new AexpUsageException(
                    $"The verifier program '{program}' resolves outside the project root; name it by an absolute path to allow that.");
            }

            return IsExecutable(resolved) ? resolved : throw new AexpUsageException("The verifier program does not exist or is not executable.", resolved);
        }

        var extensions = OperatingSystem.IsWindows()
            ? (environment("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        foreach (var directory in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            // An empty or relative entry means the current directory: never searched.
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var candidates = Path.HasExtension(program) || extensions.Length == 0
                ? [Path.Combine(directory, program)]
                : extensions.Select(extension => Path.Combine(directory, program + extension)).Prepend(Path.Combine(directory, program));
            foreach (var candidate in candidates)
            {
                if (IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new AexpUsageException($"The verifier program '{program}' was not found on PATH (the current directory is never searched).");
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    public static async Task<VerifierResult> RunAsync(VerifierSetting verifier, string program, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
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
                throw new AexpUsageException("The verifier could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AexpUsageException("The verifier could not be started.", ex.Message);
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
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new VerifierResult(-1, TimedOut: true);
        }

        // A process the verifier left behind may hold its output open; the verdict does not wait for it.
        await Task.WhenAny(Task.WhenAll(drainOutput, drainError), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        return new VerifierResult(process.ExitCode, TimedOut: false);
    }

    /// <summary>Kills the verifier and every process it started, and waits (bounded) for it to go.</summary>
    internal static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended on its own in the meantime, or part of the tree was already gone; make sure of the root.
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (inner is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        try
        {
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
