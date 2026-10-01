using System.Runtime.CompilerServices;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Runs this whole suite with PostgreSQL row-level security <b>on</b> when <c>AGENTEXPERIENCE_TEST_RLS=on</c>
/// (story 15.1): every <see cref="ExperienceApplicationRoleOptions"/> the fixture and the tests construct then
/// starts with <see cref="ExperienceApplicationRoleOptions.EnableRowLevelSecurity"/> set, through the internal,
/// test-only <see cref="ExperienceApplicationRoleOptions.TestSuiteRowLevelSecurity"/>, so every store runs as the
/// application role behind the policies. CI runs the suite in plaintext, crypto-shredding and row-level security
/// modes. The precedent is <see cref="EncryptionMode"/>.
/// </summary>
public static class RowLevelSecurityMode
{
    /// <summary>The environment variable that switches the suite to row-level security mode.</summary>
    public const string Variable = "AGENTEXPERIENCE_TEST_RLS";

    /// <summary>Whether this run is the row-level security run.</summary>
    public static bool IsOn { get; } =
        string.Equals(Environment.GetEnvironmentVariable(Variable), "on", StringComparison.OrdinalIgnoreCase);

#pragma warning disable CA2255 // A test assembly is the one place a module initializer is the right seam: it runs before any fixture.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (IsOn)
        {
            ExperienceApplicationRoleOptions.TestSuiteRowLevelSecurity = true;
        }
    }
}
