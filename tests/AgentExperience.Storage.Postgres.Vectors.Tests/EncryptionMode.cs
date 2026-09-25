using System.Runtime.CompilerServices;
using AgentExperience.Core.KeyManagement;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Runs this whole, unmodified suite in <b>encrypted mode</b> when <c>AGENTEXPERIENCE_TEST_ENCRYPTION=on</c>:
/// every component the tests construct without an <see cref="ExperienceEncryption"/> then gets
/// <see cref="Shared"/>, through the internal, test-only <see cref="ExperienceEncryption.TestSuiteDefault"/>.
/// CI runs the suite both ways.
/// </summary>
public static class EncryptionMode
{
    /// <summary>The environment variable that switches the suite to encrypted mode.</summary>
    public const string Variable = "AGENTEXPERIENCE_TEST_ENCRYPTION";

    /// <summary>One key store for the whole run, as one deployment shares one.</summary>
    public static ExperienceEncryption Shared { get; } = new(new EnvelopeExperienceKeyStore(
        LocalExperienceKeyEncryptionKey.Generate("suite-kek-1"),
        new InMemoryExperienceWrappedKeyRepository()));

    /// <summary>Whether this run is the encrypted-mode run.</summary>
    public static bool IsOn { get; } =
        string.Equals(Environment.GetEnvironmentVariable(Variable), "on", StringComparison.OrdinalIgnoreCase);

#pragma warning disable CA2255 // A test assembly is the one place a module initializer is the right seam: it runs before any fixture.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (IsOn)
        {
            ExperienceEncryption.TestSuiteDefault = Shared;
        }
    }

    /// <summary>
    /// What an erasure outside the library has to declare since 0016: that its transaction destroys the key of
    /// any sealed record it tombstones. Harmless for a plaintext row, so the raw purges in these tests set it in
    /// both suite modes, exactly as the encrypted-mode store does before it calls the purge function.
    /// </summary>
    public static async Task DeclareKeyDestructionAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction)
    {
        await using var marker = new Npgsql.NpgsqlCommand("SET LOCAL agent_experience.erasure_destroys_key = 'on'", connection, transaction);
        await marker.ExecuteNonQueryAsync();
    }
}
