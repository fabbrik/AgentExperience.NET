using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Proves <c>AgentExperience.Storage.Postgres</c> uses plain Npgsql, DbUp for schema migrations, and
/// the dependency-injection <em>abstractions</em> its own <c>AddAgentExperiencePostgresStore</c>
/// extension needs -- and nothing else: no MAF, EF Core, Dapper, Pgvector, or model-provider
/// dependency, in either its compiled references or its csproj. The DI package is abstractions only
/// (no container, no hosting), so the adapter still imposes no composition root on a host.
/// </summary>
public class DependencyBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "Microsoft.Agents", // Microsoft Agent Framework (MAF)
        "Microsoft.EntityFrameworkCore", // EF Core
        "Dapper",
        "Pgvector",
        "VectorData",
        "Microsoft.Extensions.AI", // model-provider / AI abstractions
        "Microsoft.SemanticKernel",
        "OpenAI",
        "Azure.AI",
        "Anthropic",
        "OpenTelemetry", // the library emits through the BCL; exporting is the host's decision
        "AgentExperience.Core", // this adapter depends on the ports in Abstractions, never on Core
    ];

    [Fact]
    public void Storage_Postgres_does_not_reference_a_forbidden_assembly()
    {
        var referenced = typeof(PostgresExperienceRecordStore).Assembly.GetReferencedAssemblies();
        Assert.Contains(referenced, a => a.Name == "Npgsql");

        foreach (var name in referenced.Select(a => a.Name ?? string.Empty))
        {
            foreach (var forbidden in Forbidden)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.Storage.Postgres references '{name}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    public void Storage_Postgres_csproj_declares_only_the_exact_Npgsql_DbUp_and_DI_abstractions_pins()
    {
        var csprojPath = GetCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.Storage.Postgres.csproj at '{csprojPath}'.");

        var packages = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => $"{e.Attribute("Include")?.Value} {e.Attribute("Version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "Microsoft.Extensions.DependencyInjection.Abstractions [10.0.12]",
                "Npgsql [10.0.3]",
                "dbup-core [6.1.1]",
                "dbup-postgresql [7.0.1]",
            ],
            packages);
    }

    private static string GetCsprojPath([CallerFilePath] string testSourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourceFilePath)!, "..", "..", "src", "AgentExperience.Storage.Postgres", "AgentExperience.Storage.Postgres.csproj"));
}
