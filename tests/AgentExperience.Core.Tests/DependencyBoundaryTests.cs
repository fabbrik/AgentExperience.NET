using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Proves <c>AgentExperience.Core</c> has no dependency on MAF, EF Core, Npgsql, OpenTelemetry, or
/// a model-provider package (AD-1) -- its only allowed dependencies are
/// <c>AgentExperience.Abstractions</c> and <c>Microsoft.Extensions.Compliance.Redaction</c> (plus
/// that package's own transitive <c>Microsoft.Extensions.*</c> configuration/DI/options graph).
/// Mirrors <c>AgentExperience.Abstractions.Tests/DependencyBoundaryTests.cs</c>. This runs in CI on
/// every push/PR so the boundary cannot silently regress as later stories/adapters are added to
/// the solution.
/// </summary>
public class DependencyBoundaryTests
{
    /// <summary>
    /// Case-insensitive substrings that must never appear in a referenced assembly name of
    /// <c>AgentExperience.Core</c>.
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

    private static AssemblyName[] CoreReferencedAssemblies => typeof(DefaultSanitizer).Assembly.GetReferencedAssemblies();

    [Fact]
    public void AgentExperience_Core_does_not_reference_MAF_EFCore_Npgsql_OpenTelemetry_or_a_model_provider_assembly()
    {
        var referenced = CoreReferencedAssemblies;
        Assert.NotEmpty(referenced); // sanity: the assembly does reference something (at least Abstractions)

        foreach (var assemblyName in referenced)
        {
            var name = assemblyName.Name ?? string.Empty;
            foreach (var forbidden in ForbiddenAssemblyNameSubstrings)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.Core references '{name}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    public void AgentExperience_Core_csproj_declares_no_forbidden_PackageReference()
    {
        // GetReferencedAssemblies() only reports assemblies the compiled output actually binds
        // to; a declared-but-unused PackageReference would pass the assembly-based check above
        // silently. Reading the csproj's declared <PackageReference> entries directly closes that
        // gap.
        var csprojPath = GetCoreCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.Core.csproj at '{csprojPath}'.");

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
                    $"AgentExperience.Core.csproj declares PackageReference '{include}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    private static string GetCoreCsprojPath([CallerFilePath] string testSourceFilePath = "")
    {
        var testsProjectDirectory = Path.GetDirectoryName(testSourceFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testsProjectDirectory, "..", "..", "src", "AgentExperience.Core", "AgentExperience.Core.csproj"));
    }
}
