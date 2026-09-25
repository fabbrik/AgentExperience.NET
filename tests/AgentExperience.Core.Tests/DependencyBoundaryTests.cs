using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Proves <c>AgentExperience.Core</c> has no dependency on MAF, EF Core, Npgsql, DbUp, OpenTelemetry,
/// or a model-provider package (AD-1) -- its only allowed dependencies are
/// <c>AgentExperience.Abstractions</c>, <c>Microsoft.Extensions.Compliance.Redaction</c>, and
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> (plus the redaction package's own
/// transitive <c>Microsoft.Extensions.*</c> configuration/DI/options graph). The DI package is
/// abstractions only -- no container, no hosting -- and exists so Core can ship its own
/// <c>AddAgentExperienceCore</c> registration extension without a host guessing concrete types.
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
        "dbup", // DbUp schema migrations (adapter-only, see AgentExperience.Storage.Postgres)
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

    [Fact]
    [Trait("Category", "DeclaredPins")]
    public void AgentExperience_Core_csproj_declares_exactly_the_allowed_PackageReferences()
    {
        // The forbidden-substring checks above cannot catch a newly added package that is merely
        // unwanted rather than forbidden. Pinning the whole declared set makes every future addition
        // a deliberate, reviewed change to this list.
        var declared = XDocument.Load(GetCoreCsprojPath())
            .Descendants("PackageReference")
            .Select(element => $"{element.Attribute("Include")?.Value} {element.Attribute("Version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        // Both are floors (story 6.3, KL-13): the version CI proves, with the newest in the same major
        // proven by CI's floating leg. The DI abstractions floor matches the two storage packages', so
        // no host can resolve a lower one for one package than for another. Excluded from the floating
        // leg by its trait, because that leg rewrites these versions on purpose.
        Assert.Equal(
            [
                "Microsoft.Extensions.Compliance.Redaction 10.10.0",
                "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
            ],
            declared);
    }

    private static string GetCoreCsprojPath([CallerFilePath] string testSourceFilePath = "")
    {
        var testsProjectDirectory = Path.GetDirectoryName(testSourceFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testsProjectDirectory, "..", "..", "src", "AgentExperience.Core", "AgentExperience.Core.csproj"));
    }
}
