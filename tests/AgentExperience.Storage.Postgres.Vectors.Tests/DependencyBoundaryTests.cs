using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Proves <c>AgentExperience.Storage.Postgres.Vectors</c> takes exactly the floors Story 1.7's proof
/// verified -- plain Npgsql, Pgvector, the model-provider <em>abstractions</em>, and the
/// dependency-injection abstractions its own registration extension needs -- and nothing else: no
/// MAF, EF Core, Dapper, Semantic Kernel, or concrete model-provider dependency.
/// </summary>
/// <remarks>
/// This package exists precisely so those dependencies stay out of
/// <c>AgentExperience.Storage.Postgres</c>, whose own boundary test pins its package set exactly and
/// forbids <c>Pgvector</c> and <c>Microsoft.Extensions.AI</c>. Splitting the two means neither list
/// has to move, and a host that wants only canonical storage never pulls a vector or AI dependency in.
/// </remarks>
public class DependencyBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "Microsoft.Agents", // Microsoft Agent Framework (MAF)
        "Microsoft.EntityFrameworkCore", // EF Core
        "Dapper",
        "Microsoft.SemanticKernel", // the legacy PgVector connector's home
        "OpenAI",
        "Azure.AI",
        "Anthropic",
    ];

    [Fact]
    public void Storage_Postgres_Vectors_does_not_reference_a_forbidden_assembly()
    {
        var referenced = typeof(PostgresExperienceEmbeddingIndex).Assembly.GetReferencedAssemblies();
        Assert.Contains(referenced, a => a.Name == "Npgsql");
        Assert.Contains(referenced, a => a.Name == "Pgvector");

        foreach (var name in referenced.Select(a => a.Name ?? string.Empty))
        {
            foreach (var forbidden in Forbidden)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"AgentExperience.Storage.Postgres.Vectors references '{name}', which matches forbidden dependency '{forbidden}'.");
            }
        }
    }

    [Fact]
    [Trait("Category", "DeclaredPins")]
    public void Storage_Postgres_Vectors_csproj_declares_only_the_verified_floors()
    {
        var csprojPath = GetCsprojPath();
        Assert.True(File.Exists(csprojPath), $"Could not locate AgentExperience.Storage.Postgres.Vectors.csproj at '{csprojPath}'.");

        var packages = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => $"{e.Attribute("Include")?.Value} {e.Attribute("Version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        // Every version here is a floor (story 6.3, KL-13) that Story 1.7's executable Postgres/pgvector
        // proof and CI verify; CI's floating leg proves the newest in each major, and is kept away from
        // this test by the trait above because it rewrites these versions on purpose.
        Assert.Equal(
            [
                "Microsoft.Extensions.AI.Abstractions 10.10.0",
                "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
                "Npgsql 10.0.3",
                "Pgvector 0.3.2",
            ],
            packages);
    }

    [Fact]
    public void The_canonical_store_package_still_takes_no_vector_or_model_provider_dependency()
    {
        // The whole reason this package is separate. If this ever fails, the split has been undone.
        var referenced = typeof(PostgresExperienceRecordStore).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referenced, name => name.Contains("Pgvector", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.Contains("VectorData", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.Contains("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_still_takes_no_model_provider_dependency_although_it_now_embeds()
    {
        // Core embeds through its own domain-typed port; the model-provider abstraction stops at the
        // adapter edge, which is what keeps AD-1 true.
        var referenced = typeof(Core.Indexing.ExperienceIndexingService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referenced, name => name.Contains("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.Contains("Pgvector", StringComparison.OrdinalIgnoreCase));

        // Same for Abstractions, which stays BCL-only even though it now declares the embedding ports.
        Assert.All(
            typeof(IExperienceEmbeddingIndex).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty),
            name => Assert.StartsWith("System.", name, StringComparison.Ordinal));
    }

    private static string GetCsprojPath([CallerFilePath] string testSourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourceFilePath)!,
            "..",
            "..",
            "src",
            "AgentExperience.Storage.Postgres.Vectors",
            "AgentExperience.Storage.Postgres.Vectors.csproj"));
}
