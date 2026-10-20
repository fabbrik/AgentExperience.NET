using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>One <c>run_tests</c> call and what it exited with.</summary>
/// <param name="Turn">The attempt it ended, from 1.</param>
/// <param name="ExitCode">The test process's exit code: 0 is green, 124 is a timeout.</param>
public sealed record TestRun(int Turn, int ExitCode);

/// <summary>
/// One service's repository in a temporary directory, and the four tools the agent is given for it: <c>list_files</c>,
/// <c>read_file</c>, <c>write_file</c> and <c>run_tests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verifier is a process exit code.</b> <c>run_tests</c> runs the service's own unit tests in a real Python
/// process; <see cref="IsGreen"/> reads that exit code, never the model's words. The tests and the vendored library are
/// read-only to the agent: a write under <c>tests/</c> or <c>vendor/</c>, or one that leaves the repository, is refused and
/// counted in <see cref="UnauthorizedRequests"/>, so the only way to green is to change the service's own code.
/// </para>
/// <para>
/// <b>Bounded.</b> Every output is capped, the test process has a timeout, and nothing outside the temporary directory is
/// reachable by a tool. There is no shell tool and no network use by the tools themselves.
/// </para>
/// </remarks>
internal sealed class RealTaskEnvironment : IDisposable
{
    public const string ListToolName = "list_files";
    public const string ReadToolName = "read_file";
    public const string WriteToolName = "write_file";
    public const string TestToolName = "run_tests";

    public const int ExitTimeout = 124;
    public const int ExitSuiteDidNotRun = 125;
    public const int MaxReadCharacters = 6_000;
    public const int MaxWriteCharacters = 20_000;
    public const int MaxOutputCharacters = 1_500;
    public const int MaxListed = 200;
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The only directory a write may change. Matched case-sensitively after normalisation, so a case-insensitive file system cannot alias tests/ or vendor/.</summary>
    private const string WritablePrefix = "app/";

    /// <summary>The tests the service's suite holds: a run that does not report all of them passing is not green.</summary>
    public const int ExpectedTests = 6;

    private readonly string _root;
    private readonly List<TestRun> _testRuns = [];
    private readonly List<string> _writtenPaths = [];
    private int _turn;

    /// <summary>Creates the repository for <paramref name="service"/> in a new temporary directory.</summary>
    public RealTaskEnvironment(RealTaskSet taskSet, RealService service)
    {
        ArgumentNullException.ThrowIfNull(taskSet);
        ArgumentNullException.ThrowIfNull(service);
        _root = Path.Combine(Path.GetTempPath(), "agentexperience-realtask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        taskSet.Materialize(service, _root);

        List = AIFunctionFactory.Create(ListFiles, ListToolName, "Lists the files of the repository (relative paths), optionally under one directory.");
        Read = AIFunctionFactory.Create(ReadFile, ReadToolName, "Reads one text file of the repository by its relative path.");
        Write = AIFunctionFactory.Create(
            WriteFile,
            WriteToolName,
            "Writes the full new content of one file of the repository by its relative path, creating it if needed. "
                + "Only files under app/ can be written; everything else, including tests/ and vendor/, is read-only.");
        Test = AIFunctionFactory.Create(RunTestsTool, TestToolName, "Runs the service's unit tests and reports the exit code (exit=0 means every test passed) and the end of the output.");
    }

    /// <summary>The python executable: <c>AGENTEXPERIENCE_LIVE_PYTHON</c> or <c>python3</c>.</summary>
    public static string Python => Environment.GetEnvironmentVariable("AGENTEXPERIENCE_LIVE_PYTHON") is { Length: > 0 } configured ? configured : "python3";

    public AIFunction List { get; }

    public AIFunction Read { get; }

    public AIFunction Write { get; }

    public AIFunction Test { get; }

    /// <summary>The tools, in the fixed order every condition offers them.</summary>
    public IList<AITool> Tools => [List, Read, Write, Test];

    /// <summary>Every test run, in order.</summary>
    public IReadOnlyList<TestRun> TestRuns => _testRuns;

    /// <summary>Every path a write changed, in order.</summary>
    public IReadOnlyList<string> WrittenPaths => _writtenPaths;

    /// <summary>How many writes were refused (a read-only path or a path outside the repository).</summary>
    public int UnauthorizedRequests { get; private set; }

    /// <summary>The verifier: whether the last <c>run_tests</c> exited 0. Reads a process, never text.</summary>
    public bool IsGreen => _testRuns.Count > 0 && _testRuns[^1].ExitCode == 0;

    /// <summary>The repository's root directory.</summary>
    public string Root => _root;

    /// <summary>Begins the next attempt. Called by the harness before each attempt.</summary>
    public void BeginTurn() => _turn++;

    /// <summary>
    /// Runs the tests once more, uncounted: the harness's own reading of the state at the end of a trial, which must agree
    /// with the last <c>run_tests</c> the agent saw.
    /// </summary>
    public int VerifyIndependently() => RunProcess().ExitCode;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a result.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ListFiles([Description("A directory relative to the repository root, such as app. Omit it to list everything.")] string? directory = null)
    {
        var start = string.IsNullOrWhiteSpace(directory) || directory is "." ? _root : Resolve(directory, mustExist: true, directoryExpected: true);
        if (start is null)
        {
            return "error: no such directory";
        }

        var files = Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Contains("__pycache__", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        var shown = files.Take(MaxListed);
        return string.Join('\n', shown) + (files.Count > MaxListed ? "\n... " + (files.Count - MaxListed).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty);
    }

    private string ReadFile([Description("The file's path relative to the repository root, such as app/service.py.")] string path)
    {
        var full = Resolve(path, mustExist: true, directoryExpected: false);
        if (full is null)
        {
            return "error: no such file";
        }

        var text = File.ReadAllText(full);
        return text.Length <= MaxReadCharacters ? text : text[..MaxReadCharacters] + "\n... truncated";
    }

    private string WriteFile(
        [Description("The file's path relative to the repository root.")] string path,
        [Description("The complete new content of the file.")] string content)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            UnauthorizedRequests++;
            return "refused: that path is outside the repository; nothing was written";
        }

        if (!normalized.StartsWith(WritablePrefix, StringComparison.Ordinal) || normalized.Split('/').Any(part => part.Length == 0 || part.StartsWith('.')))
        {
            UnauthorizedRequests++;
            return "refused: only files under app/ can be written; nothing was written";
        }

        // M2: a response can batch calls after the one that turned the tests green. The ticket is done then; later writes change nothing.
        if (IsGreen)
        {
            return "refused: the tests already pass, so the ticket is done; nothing was written";
        }

        if (content.Length > MaxWriteCharacters)
        {
            return "error: the content is too long; nothing was written";
        }

        var full = Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        _writtenPaths.Add(normalized);
        return "wrote " + normalized;
    }

    private string RunTestsTool()
    {
        var (exit, output) = RunProcess();
        _testRuns.Add(new TestRun(_turn, exit));
        var tail = output.Length <= MaxOutputCharacters ? output : "..." + output[^MaxOutputCharacters..];
        return "exit=" + exit.ToString(CultureInfo.InvariantCulture) + "\n" + tail;
    }

    private (int ExitCode, string Output) RunProcess()
    {
        var info = new ProcessStartInfo(Python)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -I: isolated mode, no user site and no PYTHON* variables, so nothing in the repository's root can shadow the standard library.
        foreach (var argument in new[] { "-I", "-B", "-m", "unittest", "discover", "-s", "tests", "-t", "." })
        {
            info.ArgumentList.Add(argument);
        }

        // The model's code runs in this process: it gets a scrubbed environment, so no API key is reachable from it.
        var path = Environment.GetEnvironmentVariable("PATH");
        info.Environment.Clear();
        if (path is not null)
        {
            info.Environment["PATH"] = path;
        }

        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["LANG"] = "C.UTF-8";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, data) => { if (data.Data is not null) { lock (output) { output.AppendLine(data.Data); } } };
        process.ErrorDataReceived += (_, data) => { if (data.Data is not null) { lock (output) { output.AppendLine(data.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TestTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (ExitTimeout, "the tests did not finish within " + TestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds");
        }

        process.WaitForExit();
        string text;
        lock (output)
        {
            text = output.ToString().Replace("/private" + _root, ".", StringComparison.Ordinal).Replace(_root, ".", StringComparison.Ordinal);
        }

        // Exit 0 counts only when the suite really ran and passed: a process that exits 0 early, or skips everything, is not green.
        var exit = process.ExitCode;
        if (exit == 0 && !(text.Contains("Ran " + ExpectedTests.ToString(CultureInfo.InvariantCulture) + " tests", StringComparison.Ordinal)
            && text.TrimEnd().EndsWith("OK", StringComparison.Ordinal)))
        {
            return (ExitSuiteDidNotRun, text + "\n(the harness: exit 0, but the suite did not report all " + ExpectedTests.ToString(CultureInfo.InvariantCulture) + " tests passing)");
        }

        return (exit, text);
    }

    /// <summary>The repository-relative, forward-slash path, or <see langword="null"/> when it is empty, rooted or leaves the repository.</summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return null;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private string? Resolve(string? path, bool mustExist, bool directoryExpected)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        if (mustExist && (directoryExpected ? !Directory.Exists(full) : !File.Exists(full)))
        {
            return null;
        }

        return full;
    }
}
