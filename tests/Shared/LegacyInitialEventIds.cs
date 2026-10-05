using System.Security.Cryptography;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// The initial lifecycle event ID releases up to and including 0.1.0-preview.6 derived from the run alone:
/// SHA-256 over the fixed derivation namespace, the run ID and the purpose tag 2, stamped as an RFC 9562
/// version 8 UUID. Story 17.6 mixed the scope in, so the library no longer derives this value; it is spelled
/// out here, once, to stand in for records an earlier release wrote, and pinned by <see cref="GoldenRunId"/>.
/// </summary>
internal static class LegacyInitialEventIds
{
    /// <summary>A fixed run ID whose run-only initial event ID is <see cref="GoldenInitialEventId"/>.</summary>
    public static readonly Guid GoldenRunId = new("6f1c2a90-4b7e-4d3a-9c55-0e8f7a6b5c41");

    /// <summary>
    /// What 0.1.0-preview.6 (commit 450e813) derived for <see cref="GoldenRunId"/>, computed from that source and
    /// confirmed against the published 0.1.0-preview.1 package, whose derivation is the same.
    /// </summary>
    public static readonly Guid GoldenInitialEventId = new("8a8b8372-2ac7-8c7e-8261-0eef7e461a50");

    public static Guid For(Guid runId)
    {
        Span<byte> input = stackalloc byte[33];
        new Guid("0b6a8a3f-1c2d-4f5e-9a70-3d1c9f2b8e41").TryWriteBytes(input[..16], bigEndian: true, out _);
        runId.TryWriteBytes(input.Slice(16, 16), bigEndian: true, out _);
        input[32] = 2;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }
}
