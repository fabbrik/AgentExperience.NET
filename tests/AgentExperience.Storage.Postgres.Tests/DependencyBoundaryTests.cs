using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Proves <c>AgentExperience.Storage.Postgres</c> uses plain Npgsql only: no MAF, EF Core, Dapper,
/// Pgvector, or model-provider dependency, in either its compiled references or its csproj.
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
    public void Storage_Postgres_csproj_declares_only_an_exact_Npgsql_pin()
    {
        var csprojPath = GetCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.Storage.Postgres.csproj at '{csprojPath}'.");

        var packages = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => (Include: e.Attribute("Include")?.Value ?? string.Empty, Version: e.Attribute("Version")?.Value))
            .ToList();

        var npgsql = Assert.Single(packages);
        Assert.Equal("Npgsql", npgsql.Include);
        Assert.Equal("[10.0.3]", npgsql.Version);
    }

    private static string GetCsprojPath([CallerFilePath] string testSourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourceFilePath)!, "..", "..", "src", "AgentExperience.Storage.Postgres", "AgentExperience.Storage.Postgres.csproj"));
}
