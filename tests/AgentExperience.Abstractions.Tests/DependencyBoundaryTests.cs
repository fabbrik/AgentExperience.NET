using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Proves <c>AgentExperience.Abstractions</c> has no dependency on MAF, EF Core, Npgsql,
/// OpenTelemetry, or a model-provider package (AC5). This runs in CI on every push/PR so the
/// boundary cannot silently regress as later stories/adapters are added to the solution.
/// </summary>
public class DependencyBoundaryTests
{
    /// <summary>
    /// Case-insensitive substrings that must never appear in a referenced assembly name of
    /// <c>AgentExperience.Abstractions</c>.
    /// </summary>
    private static readonly string[] ForbiddenAssemblyNameSubstrings =
    [
        "Microsoft.Agents", // Microsoft Agent Framework (MAF)
        "Microsoft.EntityFrameworkCore", // EF Core
        "Npgsql", // PostgreSQL driver
        "OpenTelemetry",
        "Microsoft.Extensions.AI", // model-provider / AI abstractions
        "Microsoft.SemanticKernel",
        "OpenAI",
        "Azure.AI",
        "Anthropic",
    ];

    private static AssemblyName[] AbstractionsReferencedAssemblies => typeof(ExperienceRun).Assembly.GetReferencedAssemblies();

    [Fact]
    public void AgentExperience_Abstractions_does_not_reference_MAF_EFCore_Npgsql_OpenTelemetry_or_a_model_provider_assembly()
    {
        var referenced = AbstractionsReferencedAssemblies;
        Assert.NotEmpty(referenced); // sanity: the assembly does reference the BCL

        foreach (var assemblyName in referenced)
        {
            var name = assemblyName.Name ?? string.Empty;
            foreach (var forbidden in ForbiddenAssemblyNameSubstrings)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.Abstractions references '{name}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    public void AgentExperience_Abstractions_only_references_the_BCL()
    {
        // Zero PackageReferences means every referenced assembly should come from the runtime
        // itself (System.*, netstandard, mscorlib) rather than a third-party package.
        var referenced = AbstractionsReferencedAssemblies;

        foreach (var assemblyName in referenced)
        {
            var name = assemblyName.Name ?? string.Empty;
            var isBcl = name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("System", StringComparison.OrdinalIgnoreCase);

            Assert.True(isBcl, $"AgentExperience.Abstractions references non-BCL assembly '{name}'.");
        }
    }

    [Fact]
    public void AgentExperience_Abstractions_csproj_declares_no_forbidden_PackageReference()
    {
        // GetReferencedAssemblies() only reports assemblies the compiled output actually binds
        // to; a declared-but-unused PackageReference (e.g. a forbidden package added to the
        // csproj but never referenced by any type) would pass the assembly-based checks above
        // silently. Reading the csproj's declared <PackageReference> entries directly closes
        // that gap.
        var csprojPath = GetAbstractionsCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.Abstractions.csproj at '{csprojPath}'.");

        var declaredPackageReferences = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        foreach (var include in declaredPackageReferences)
        {
            foreach (var forbidden in ForbiddenAssemblyNameSubstrings)
            {
                Assert.False(
                    include.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.Abstractions.csproj declares PackageReference '{include}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    private static string GetAbstractionsCsprojPath([CallerFilePath] string testSourceFilePath = "")
    {
        var testsProjectDirectory = Path.GetDirectoryName(testSourceFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testsProjectDirectory, "..", "..", "src", "AgentExperience.Abstractions", "AgentExperience.Abstractions.csproj"));
    }
}
