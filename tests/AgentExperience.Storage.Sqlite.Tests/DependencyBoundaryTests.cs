using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AgentExperience.Storage.Sqlite;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>
/// <c>AgentExperience.Storage.Sqlite</c> takes Abstractions, <c>Microsoft.Data.Sqlite</c> and the DI abstractions, and
/// nothing else: no Core, no other store, no model provider, no PostgreSQL driver.
/// </summary>
public sealed class DependencyBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "Microsoft.Agents", "Microsoft.EntityFrameworkCore", "Npgsql", "dbup", "Pgvector", "Microsoft.Extensions.AI",
        "OpenAI", "Azure.AI", "Anthropic", "AgentExperience.Core", "AgentExperience.Storage.Postgres", "AgentExperience.Storage.InMemory",
    ];

    [Fact]
    public void Storage_Sqlite_does_not_reference_a_forbidden_assembly()
    {
        var referenced = typeof(SqliteExperienceRecordStore).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.Contains("Microsoft.Data.Sqlite", referenced);

        foreach (var name in referenced)
        {
            Assert.DoesNotContain(Forbidden, forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    [Trait("Category", "DeclaredPins")]
    public void Storage_Sqlite_csproj_declares_only_its_floors()
    {
        var packages = XDocument.Load(CsprojPath())
            .Descendants("PackageReference")
            .Select(e => $"{e.Attribute("Include")?.Value} {e.Attribute("Version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "Microsoft.Data.Sqlite 10.0.12",
                "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
            ],
            packages);
    }

    private static string CsprojPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "AgentExperience.Storage.Sqlite", "AgentExperience.Storage.Sqlite.csproj"));
}
