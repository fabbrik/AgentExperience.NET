using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Documentation;

/// <summary>
/// The "Without Docker" commands in <c>CONTRIBUTING.md</c> cannot drift: every test project in the repository is
/// either run there (whole, or with a filter) or named as skipped; a project run whole uses no container, and in a
/// filtered project every class that uses one is excluded by that project's filter.
/// </summary>
/// <remarks>
/// The section used to be one hand-kept filter of class names, which missed container-backed classes added after it
/// was written. A test project is one whose project file sets <c>IsTestProject</c> or references
/// <c>Microsoft.NET.Test.Sdk</c>, unless it sets <c>IsTestProject</c> to false. A container-backed class is a top-level
/// class in a file with a <c>using Testcontainers...</c> directive, or in a file that names a type declared in one --
/// followed to a fixed point, so a fixture of a fixture counts. A mere mention of the word does not.
/// </remarks>
public sealed partial class ContributingWithoutDockerTests
{
    private static readonly string[] ProjectRoots = ["tests", "experiments", "samples", "benchmarks"];

    [Fact]
    public void Every_test_project_is_either_run_or_skipped_without_Docker()
    {
        var section = Section();
        var run = RunCommands(section).Select(command => command.Project).ToList();
        var skipped = Skipped(section);

        Assert.NotEmpty(run);
        Assert.NotEmpty(skipped);

        var projects = TestProjects();
        Assert.Contains("tests/AgentExperience.Core.Tests", projects);

        var problems = new List<string>();
        problems.AddRange(projects
            .Where(project => !run.Contains(project) && !skipped.Contains(project))
            .Select(project => $"{project} is neither run nor skipped."));
        problems.AddRange(run.Intersect(skipped).Select(project => $"{project} is both run and skipped."));
        problems.AddRange(run.Concat(skipped)
            .Where(project => !projects.Contains(project))
            .Select(project => $"{project} is not a test project in this repository."));
        problems.AddRange(run.GroupBy(project => project).Where(group => group.Count() > 1).Select(group => $"{group.Key} is run twice."));

        Assert.True(problems.Count == 0, "CONTRIBUTING.md \"Without Docker\" is out of date:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void A_project_run_without_Docker_runs_no_class_that_uses_a_container()
    {
        var commands = RunCommands(Section());
        Assert.Contains(commands, command => command.Filter is not null);
        Assert.Contains(commands, command => command.Filter is null);

        var problems = new List<string>();
        foreach (var (project, filter) in commands)
        {
            var classes = ContainerBackedClasses(project, problems);
            if (filter is null)
            {
                problems.AddRange(classes.Select(name => $"{project} is run whole, but {name} uses a container; filter it or skip the project."));
                continue;
            }

            var excluded = Exclusion().Matches(filter).Select(match => match.Groups[1].Value).ToList();
            if (excluded.Count == 0)
            {
                problems.Add($"The filter for {project} excludes nothing: {filter}");
            }

            if (classes.Count == 0)
            {
                problems.Add($"{project} is filtered but no class in it uses a container; run it whole.");
            }

            problems.AddRange(classes
                .Where(name => !excluded.Any(token => name.Contains(token, StringComparison.Ordinal)))
                .Select(name => $"{project}: {name} uses a container but its filter ({filter}) does not exclude it."));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The "Without Docker" part of CONTRIBUTING.md, up to the next heading.</summary>
    private static string Section()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "CONTRIBUTING.md")).ReplaceLineEndings("\n");
        var start = text.IndexOf("**Without Docker**", StringComparison.Ordinal);
        Assert.True(start >= 0, "CONTRIBUTING.md has no \"Without Docker\" section.");
        var end = text.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static List<(string Project, string? Filter)> RunCommands(string section) =>
        RunCommand().Matches(section)
            .Select(match => (match.Groups["project"].Value, match.Groups["filter"].Success ? match.Groups["filter"].Value : null))
            .ToList();

    private static List<string> Skipped(string section)
    {
        var start = section.IndexOf("Skipped without Docker:", StringComparison.Ordinal);
        Assert.True(start >= 0, "The \"Without Docker\" section has no \"Skipped without Docker:\" list.");
        var end = section.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = end < 0 ? section[start..] : section[start..end];
        return QuotedProject().Matches(paragraph).Select(match => match.Groups[1].Value).ToList();
    }

    /// <summary>Repository-relative directories, with forward slashes, of every project that sets <c>IsTestProject</c>.</summary>
    private static List<string> TestProjects() =>
        ProjectRoots
            .Select(root => Path.Combine(RepositoryRoot.Path, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return !NotATestProject().IsMatch(text)
                    && (IsTestProject().IsMatch(text) || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal));
            })
            .Select(file => Path.GetRelativePath(RepositoryRoot.Path, Path.GetDirectoryName(file)!).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The top-level classes of a project that use a container: declared in a file with a Testcontainers using
    /// directive, or in a file that names a type declared in such a file, followed until nothing new is found. A
    /// container-using file that declares no top-level class is reported in <paramref name="problems"/>.
    /// </summary>
    private static List<string> ContainerBackedClasses(string project, List<string> problems)
    {
        var directory = Path.Combine(RepositoryRoot.Path, project);
        var projectFile = Directory.EnumerateFiles(directory, "*.csproj").Single();
        if (GlobalTestcontainersUsing().IsMatch(File.ReadAllText(projectFile)))
        {
            problems.Add($"{project} imports Testcontainers globally, so container use cannot be told apart per file; use a using directive in each file.");
        }

        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToDictionary(file => file, File.ReadAllText);

        var containerFiles = files.Keys.Where(file => TestcontainersUsing().IsMatch(files[file])).ToHashSet();
        var containerTypes = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            foreach (var file in containerFiles)
            {
                containerTypes.UnionWith(AnyClass().Matches(files[file]).Select(match => match.Groups[1].Value));
            }

            var reached = files.Keys
                .Where(file => !containerFiles.Contains(file)
                    && containerTypes.Any(type => Regex.IsMatch(files[file], $@"\b{Regex.Escape(type)}\b")))
                .ToList();
            if (reached.Count == 0)
            {
                break;
            }

            containerFiles.UnionWith(reached);
        }

        var classes = new List<string>();
        foreach (var file in containerFiles.Order(StringComparer.Ordinal))
        {
            var declared = TopLevelClass().Matches(files[file]).Select(match => match.Groups[1].Value).ToList();
            if (declared.Count == 0)
            {
                problems.Add($"{Path.GetRelativePath(RepositoryRoot.Path, file).Replace('\\', '/')} uses a container but declares no top-level class this check can name.");
            }

            classes.AddRange(declared);
        }

        return classes.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^dotnet test (?<project>[\w./-]+)(?: --filter ""(?<filter>[^""]+)"")?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex RunCommand();

    [GeneratedRegex(@"`((?:tests|experiments|samples|benchmarks)/[\w./-]+)`")]
    private static partial Regex QuotedProject();

    [GeneratedRegex(@"FullyQualifiedName!~([\w.]+)")]
    private static partial Regex Exclusion();

    [GeneratedRegex(@"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase)]
    private static partial Regex IsTestProject();

    [GeneratedRegex(@"<IsTestProject>\s*false\s*</IsTestProject>", RegexOptions.IgnoreCase)]
    private static partial Regex NotATestProject();

    // Actual use: a using directive (plain, static or aliased), not the word in a comment or a string.
    [GeneratedRegex(@"^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?:\w+[ \t]*=[ \t]*)?Testcontainers\b", RegexOptions.Multiline)]
    private static partial Regex TestcontainersUsing();

    [GeneratedRegex(@"<Using\s+Include=""Testcontainers", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalTestcontainersUsing();

    // A public or internal class at any depth: the types another file could name. Private nested types cannot be.
    [GeneratedRegex(@"^[ \t]*(?:public|internal) (?:sealed |abstract |static |partial )*class\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex AnyClass();

    // File-scoped namespaces throughout, so a top-level class starts at column 0; nested types are indented and
    // inherit their outer class's name in FullyQualifiedName.
    [GeneratedRegex(@"^(?:public |internal )?(?:sealed |abstract |static |partial )*class\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex TopLevelClass();
}
