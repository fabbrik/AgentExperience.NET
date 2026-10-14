using AgentExperience.Abstractions;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The fixed values every contract case shares, so no fixture holds a machine-specific value: one scope, one
/// environment fingerprint, one provenance and one clock.
/// </summary>
internal static class Fixed
{
    /// <summary>The scope every case's records live in.</summary>
    public static Scope Scope { get; } = new("contract-tenant", "contract-app", "contract-project", AgentId: "refund-agent");

    /// <summary>The environment fingerprint every captured run reports. Nothing here is read from the machine.</summary>
    public static EnvironmentFingerprint Environment { get; } = new(
        HostName: "contract-host",
        RuntimeVersion: "runtime-1",
        OperatingSystem: "linux",
        ApplicationVersion: "1.0.0",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "eu-west" });

    /// <summary>The instant every case starts from: 2026-03-02T09:00:00Z.</summary>
    public static DateTimeOffset T0 { get; } = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A capture provenance recorded at <paramref name="at"/>.</summary>
    public static Provenance Provenance(DateTimeOffset at) => new("contract-fixture", "1", at, CorrelationId: null);

    /// <summary>A deterministic GUID: <paramref name="family"/> and <paramref name="n"/> spelled into its hex digits, readable in a diff.</summary>
    public static Guid Id(int family, int n) => Guid.Parse($"{family:x8}-0000-4000-8000-{n:x12}");

    /// <summary>The authorization a host established for <see cref="Scope"/>: exactly that scope.</summary>
    public static AuthorizationContext Authorization { get; } = new(
        Scope.TenantId,
        "contract-principal",
        [],
        T0,
        Scope.ApplicationId,
        Scope.ProjectId,
        Scope.TeamId,
        Scope.AgentId,
        Scope.UserId);
}
