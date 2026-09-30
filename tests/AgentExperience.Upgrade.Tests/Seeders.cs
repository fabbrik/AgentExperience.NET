using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// Builds and runs the seeder programs. Each is restored in locked mode from its committed lock file (CI restores them
/// before the test step, so the packages are already in the global cache and this needs no network), and built once
/// per test run into a directory of this run's own, outside the source tree, which is deleted when the run ends.
/// </summary>
internal static class Seeders
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Built = new(StringComparer.Ordinal);

    private static readonly Lazy<string> RunDirectory = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aen-upgrade-seeders-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: it is in the temporary directory.
            }
        };
        return directory;
    });

    /// <summary>Runs <paramref name="preview"/>'s seeder with <paramref name="arguments"/>; throws, with its output, if it fails.</summary>
    public static async Task RunAsync(PublishedPreview preview, params string[] arguments)
    {
        var assembly = await BuiltAsync(preview);
        var (exitCode, output) = await RunProcessAsync(["exec", assembly, .. arguments], SeedTimeout);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"The {preview.Version} seeder exited with {exitCode}:{Environment.NewLine}{output}");
        }
    }

    /// <summary>The built seeder, building it on first use. A failed build is not remembered, so the next case retries it.</summary>
    private static async Task<string> BuiltAsync(PublishedPreview preview)
    {
        var lazy = Built.GetOrAdd(preview.Version, _ => new Lazy<Task<string>>(() => BuildAsync(preview)));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            Built.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(preview.Version, lazy));
            throw;
        }
    }

    private static async Task<string> BuildAsync(PublishedPreview preview)
    {
        var project = preview.SeederProjectFile;
        var artifacts = Path.Combine(RunDirectory.Value, preview.SeederProject);
        string[] command =
        [
            "build", project, "--configuration", "Release", "--artifacts-path", artifacts,
            "-p:RestoreLockedMode=true", "-nodeReuse:false", "-p:UseSharedCompilation=false",
        ];
        var (exitCode, output) = await RunProcessAsync(command, BuildTimeout);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet {string.Join(' ', command)}' failed for the {preview.Version} seeder:{Environment.NewLine}{output}");
        }

        var name = Path.GetFileNameWithoutExtension(project) + ".dll";
        var assembly = Directory.GetFiles(Path.Combine(artifacts, "bin"), name, SearchOption.AllDirectories).SingleOrDefault();
        if (assembly is null)
        {
            throw new InvalidOperationException($"The {preview.Version} seeder built, but no single {name} is under '{artifacts}'.");
        }

        return assembly;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = RepositoryRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The test host runs under dotnet test, whose MSBuild settings must not leak into a separate build; and no
        // build server may outlive the command, or it would hold the redirected output open.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };
        process.ErrorDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'dotnet {string.Join(' ', arguments)}' did not finish within {timeout}.");
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    /// <summary>The dotnet host running this test, so the seeders build with the same SDK.</summary>
    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
}
