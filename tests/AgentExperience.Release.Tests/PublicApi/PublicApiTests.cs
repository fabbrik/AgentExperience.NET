using System.Reflection;
using PublicApiGenerator;
using static VerifyXunit.Verifier;

namespace AgentExperience.Release.Tests.PublicApi;

/// <summary>
/// The approval baseline of every shipping assembly's public surface (story 4.3, AD-B). Each test renders
/// one assembly's public API as C# text and compares it with the checked-in
/// <c>PublicApi/&lt;assembly&gt;.verified.txt</c>, so adding, removing, or re-shaping a public type,
/// member, parameter, default value, or attribute fails here with a reviewable diff instead of shipping
/// unnoticed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never self-updating.</b> A mismatch writes <c>&lt;assembly&gt;.received.txt</c> next to the baseline
/// and fails. To accept a deliberate change, run the one documented command and review the diff it leaves
/// in <c>git diff</c> before committing it:
/// </para>
/// <code>
/// AGENTEXPERIENCE_ACCEPT_API_CHANGES=true dotnet test tests/AgentExperience.Release.Tests --filter "FullyQualifiedName~PublicApi"
/// </code>
/// <para>
/// The environment variable is read by <see cref="VerifySettings"/> and nothing else; CI never sets it.
/// </para>
/// <para>
/// <b>One baseline for every target framework</b> (story 6.3). This project builds, and these tests run, once
/// per framework the packages ship for, and each run compares the assembly built for its own framework with
/// the same file. So the gate also asserts that the public surface is identical on every framework: a member
/// that exists on one and not another fails here rather than surprising a host that multi-targets. If that
/// ever has to change on purpose, give each framework its own file with
/// <c>UseFileName($"{name}.{framework}")</c> and say why in review.
/// </para>
/// <para>
/// Known and accepted: <c>dotnet test</c> runs the two framework test hosts in parallel, and on a mismatch
/// share the same <c>*.received.txt</c>. When only one framework diverges, the other run can clean up or
/// overwrite that file, so it may be missing or show no diff even though the test failed. The failing test's
/// framework is in the test output; to see its received file, re-run it alone with
/// <c>dotnet test --framework &lt;tfm&gt;</c>.
/// </para>
/// </remarks>
public sealed class PublicApiTests
{
    /// <summary>Only the public surface; assembly-level attributes that vary per build are left out.</summary>
    private static readonly ApiGeneratorOptions Options = new()
    {
        ExcludeAttributes =
        [
            // Stamped by the SDK from build metadata, not part of the API a consumer compiles against.
            "System.Reflection.AssemblyMetadataAttribute",
            "System.Runtime.Versioning.TargetFrameworkAttribute",
            // Names test projects, not API: renaming a test project must not fail the public-API gate.
            "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
        ],
    };

    [Fact]
    public Task AgentExperience_Abstractions() => VerifyApi(typeof(AgentExperience.Abstractions.ExperienceRun).Assembly);

    [Fact]
    public Task AgentExperience_Core() => VerifyApi(typeof(AgentExperience.Core.Sanitization.DefaultSanitizer).Assembly);

    [Fact]
    public Task AgentExperience_MicrosoftAgentFramework() =>
        VerifyApi(typeof(AgentExperience.MicrosoftAgentFramework.ExperienceCaptureOptions).Assembly);

    [Fact]
    public Task AgentExperience_Storage_Postgres() =>
        VerifyApi(typeof(AgentExperience.Storage.Postgres.PostgresExperienceRecordStore).Assembly);

    [Fact]
    public Task AgentExperience_Storage_Postgres_Vectors() =>
        VerifyApi(typeof(AgentExperience.Storage.Postgres.Vectors.PostgresExperienceEmbeddingIndex).Assembly);

    [Fact]
    public void Every_shipping_assembly_has_a_baseline_and_no_baseline_is_orphaned()
    {
        // A sixth package, or a renamed one, must not slip past the gate by simply having no test.
        var baselines = Directory.GetFiles(BaselineDirectory(), "*.verified.txt")
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AgentExperience.Abstractions.verified.txt",
                "AgentExperience.Core.verified.txt",
                "AgentExperience.MicrosoftAgentFramework.verified.txt",
                "AgentExperience.Storage.Postgres.Vectors.verified.txt",
                "AgentExperience.Storage.Postgres.verified.txt",
            ],
            baselines);

        var shipping = Directory.GetDirectories(Path.Combine(RepositoryRoot.Path, "src"))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .Select(name => $"{name}.verified.txt")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(shipping, baselines);
    }

    private static Task VerifyApi(Assembly assembly) =>
        Verify(assembly.GeneratePublicApi(Options))
            .UseDirectory(BaselineDirectory())
            .UseFileName(assembly.GetName().Name!);

    private static string BaselineDirectory() => Path.Combine(RepositoryRoot.Path, "tests", "AgentExperience.Release.Tests", "PublicApi");
}
