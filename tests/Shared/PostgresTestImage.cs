using System.Globalization;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// The PostgreSQL major version every container-backed test starts, chosen by the
/// <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> environment variable (story 6.3, KL-13). Unset, it is
/// <see cref="DefaultMajor"/>. CI runs the storage suites once per entry in <see cref="SupportedMajors"/>,
/// and <c>docs/compatibility-evidence.md</c> records the result.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into each test project that starts a container (linked, not referenced, so no test project
/// gains a dependency on another). One variable picks both images, so the stock and the pgvector image
/// in one run are always the same major: <c>pgvector/pgvector:pg{major}</c>, and stock
/// <c>postgres:{major}</c> for the tests that prove the base schema needs no extension.
/// </para>
/// <para>
/// A value outside <see cref="SupportedMajors"/> fails loudly rather than falling back: a typo in a CI
/// leg must not quietly re-run the default and report it as another version. Each suite also asserts the
/// server it reached reports this major, so the variable cannot be silently ignored either. Adding a major
/// means adding it here, to the CI matrix, and to the evidence document; a release test checks the first
/// two agree.
/// </para>
/// </remarks>
internal static class PostgresTestImage
{
    /// <summary>The environment variable that selects the major version.</summary>
    internal const string MajorVariable = "AGENTEXPERIENCE_POSTGRES_MAJOR";

    /// <summary>The major version used when <see cref="MajorVariable"/> is unset or empty.</summary>
    internal const int DefaultMajor = 16;

    /// <summary>
    /// Every PostgreSQL major the project supports: each one the PostgreSQL project still supports and
    /// <c>pgvector/pgvector</c> publishes an image for, except 14. PostgreSQL 14 is still supported upstream
    /// (until November 2026), but the journaled migration <c>0005_create_experience_grants</c> creates a
    /// <c>NULLS NOT DISTINCT</c> unique index, which is PostgreSQL 15 syntax, and journaled scripts are never
    /// edited. The evidence document records the failing run.
    /// </summary>
    internal static readonly IReadOnlyList<int> SupportedMajors = [15, 16, 17, 18];

    /// <summary>The major version this run uses.</summary>
    internal static int Major { get; } = Resolve(Environment.GetEnvironmentVariable(MajorVariable));

    /// <summary>The pgvector image for <see cref="Major"/>.</summary>
    internal static string Pgvector => $"pgvector/pgvector:pg{Major}";

    /// <summary>The stock image for <see cref="Major"/>, which has no <c>vector</c> extension available.</summary>
    internal static string Stock => $"postgres:{Major}";

    /// <summary>Parses a value of <see cref="MajorVariable"/>. Internal so the parsing can be tested directly.</summary>
    internal static int Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMajor;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !SupportedMajors.Contains(major))
        {
            throw new InvalidOperationException(
                $"{MajorVariable}='{value}' is not a supported PostgreSQL major version. Set it to one of {string.Join(", ", SupportedMajors)}, or leave it unset for {DefaultMajor}.");
        }

        return major;
    }
}
