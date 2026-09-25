using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Proves the MAF adapter takes exactly one package -- <c>Microsoft.Agents.AI</c>, at the exact
/// version its compatibility is verified against -- and that instrumenting it added none. Mirrors the
/// same assertion in <c>AgentExperience.Core.Tests</c>, <c>AgentExperience.Abstractions.Tests</c>,
/// and the two storage adapters' test projects.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because story 4.1 gave the adapter an <c>ActivitySource</c> and a <c>Meter</c>,
/// and the obvious way to do that would have been to reach for the OpenTelemetry SDK. Both types
/// ship in the shared framework instead (on <c>net9.0</c>, MAF's own graph may also bring a newer
/// <c>System.Diagnostics.DiagnosticSource</c> package, which is still not one this adapter declares), so the adapter's declared package set is
/// unchanged -- and this test is what keeps it that way when the next story adds an exporter-shaped
/// temptation.
/// </para>
/// <para>
/// The library emits; the host exports (AD-11, AD-12). An <c>OpenTelemetry.*</c> reference here would
/// force every consumer onto the SDK's version of it, which is the host's choice to make.
/// </para>
/// </remarks>
public class DependencyBoundaryTests
{
    /// <summary>
    /// Case-insensitive substrings that must never appear in a referenced assembly name, or in a
    /// declared <c>PackageReference</c>, of <c>AgentExperience.MicrosoftAgentFramework</c>. MAF itself
    /// is of course allowed here -- this is the adapter -- so the list is the storage and telemetry
    /// SDKs plus the model providers an adapter has no business binding to.
    /// </summary>
    private static readonly string[] ForbiddenAssemblyNameSubstrings =
    [
        "OpenTelemetry",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "dbup",
        "Microsoft.SemanticKernel",
        "OpenAI",
        "Azure.AI",
        "Anthropic",
        "Microsoft.Extensions.Logging",
    ];

    [Fact]
    public void AgentExperience_MicrosoftAgentFramework_does_not_reference_OpenTelemetry_a_database_or_a_model_provider_assembly()
    {
        var referenced = typeof(ExperienceContextProvider).Assembly.GetReferencedAssemblies();
        Assert.NotEmpty(referenced);

        foreach (var assemblyName in referenced)
        {
            var name = assemblyName.Name ?? string.Empty;
            foreach (var forbidden in ForbiddenAssemblyNameSubstrings)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.MicrosoftAgentFramework references '{name}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    public void AgentExperience_MicrosoftAgentFramework_csproj_declares_no_forbidden_PackageReference()
    {
        // GetReferencedAssemblies() only reports what the compiled output actually binds to, so a
        // declared-but-not-yet-used package would pass the check above in silence.
        foreach (var include in DeclaredPackageReferences().Select(element => element.Attribute("Include")?.Value ?? string.Empty))
        {
            foreach (var forbidden in ForbiddenAssemblyNameSubstrings)
            {
                Assert.False(
                    include.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.MicrosoftAgentFramework.csproj declares PackageReference '{include}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    [Trait("Category", "DeclaredPins")]
    public void AgentExperience_MicrosoftAgentFramework_csproj_declares_exactly_the_allowed_PackageReferences()
    {
        // The forbidden-substring checks above cannot catch a package that is merely unwanted rather
        // than forbidden. Pinning the whole declared set makes every future addition a deliberate,
        // reviewed change to this list -- which is exactly what "instrumentation costs no package"
        // means in practice.
        var declared = DeclaredPackageReferences()
            .Select(element => $"{element.Attribute("Include")?.Value} {element.Attribute("Version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Microsoft.Agents.AI [1.22.0]"], declared);
    }

    [Fact]
    public void ActivitySource_and_Meter_come_from_the_shared_framework()
    {
        // The reason no package was needed: both live in an assembly the shared framework of every target
        // already carries, so using them is using the BCL, not taking a dependency.
        foreach (var type in new[] { typeof(System.Diagnostics.ActivitySource), typeof(System.Diagnostics.Metrics.Meter) })
        {
            Assert.Equal("System.Diagnostics.DiagnosticSource", type.Assembly.GetName().Name);
        }
    }

    private static IEnumerable<XElement> DeclaredPackageReferences()
    {
        var csprojPath = GetAdapterCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.MicrosoftAgentFramework.csproj at '{csprojPath}'.");
        return XDocument.Load(csprojPath).Descendants("PackageReference");
    }

    private static string GetAdapterCsprojPath([CallerFilePath] string testSourceFilePath = "")
    {
        var testsProjectDirectory = Path.GetDirectoryName(testSourceFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testsProjectDirectory,
            "..",
            "..",
            "src",
            "AgentExperience.MicrosoftAgentFramework",
            "AgentExperience.MicrosoftAgentFramework.csproj"));
    }
}
