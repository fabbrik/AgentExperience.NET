using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The injection holdout (<see cref="ExperienceInjectionOptions.Holdout"/>): on a deterministic share of captured runs
/// that have a block to inject, the block is withheld, and both arms are recorded, so benefit can be measured against
/// a control group. Off by default (<see cref="Rate"/> 0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who takes part.</b> Only an invocation captured by this agent's own <c>UseExperienceCapture</c> (the same
/// condition under which an injected block is recorded as the run's exposure) and whose block names at least one
/// record. Every other invocation is injected exactly as without this option, and writes no assignment.
/// </para>
/// <para>
/// <b>How a run is assigned.</b> A pure function of the run ID and <see cref="Salt"/>: SHA-256 over the run ID's 16
/// bytes in RFC 4122 order (big-endian, as <see cref="Guid.ToByteArray(bool)"/> with <see langword="true"/> writes
/// them) followed by the salt's UTF-8 bytes; the digest's first 8 bytes, read as a big-endian unsigned integer and
/// divided by 2^64, give a fraction in [0, 1); the run is held out when that fraction is below <see cref="Rate"/>
/// (always, at a rate of 1). Every invocation of the same run lands in the same arm.
/// </para>
/// <para>
/// <b>What a held-out invocation does.</b> The block is built as usual (so what is withheld is exactly what would have
/// been delivered), then nothing is staged in the session, no message is returned, no exposure is recorded on the run,
/// nothing is recorded as injected, and the result's outcome is <see cref="InjectionOutcome.Withheld"/> with
/// <see cref="ExperienceInjectionResult.WithheldExperienceIds"/>. Every record is withheld; withdrawal notices
/// are not: they are safety notices, so a held-out invocation that owes any is given a block of those notices alone,
/// reported as <see cref="InjectionOutcome.Retracted"/> with the withheld records still named.
/// </para>
/// </remarks>
public sealed record ExperienceHoldoutOptions
{
    /// <summary>The default <see cref="Salt"/>.</summary>
    public const string DefaultSalt = "agentexperience-holdout-v1";

    /// <summary>The share of eligible runs to hold out, from 0 (off, the default) to 1 (every one).</summary>
    public double Rate { get; init; }

    /// <summary>
    /// Mixed into every assignment, so two experiments over the same runs can draw different arms. Must not be empty.
    /// Changing it re-draws every run, including runs still open.
    /// </summary>
    public string Salt { get; init; } = DefaultSalt;

    /// <summary>The holdout switched off: <see cref="Rate"/> 0.</summary>
    public static ExperienceHoldoutOptions Off { get; } = new();

    /// <summary>Whether any run can be held out or assigned at all.</summary>
    internal bool Active => Rate > 0;

    /// <summary>Refuses a rate outside [0, 1] or not a number, and a missing or empty salt.</summary>
    internal void Validate(string paramName)
    {
        if (double.IsNaN(Rate) || Rate < 0 || Rate > 1)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(Rate)}", Rate, "The holdout rate must be a number from 0 to 1.");
        }

        if (string.IsNullOrEmpty(Salt))
        {
            throw new ArgumentException("The holdout salt must not be empty.", $"{paramName}.{nameof(Salt)}");
        }
    }
}

/// <summary>The holdout's deterministic arm assignment. Pure: the same run ID, salt and rate always give the same arm.</summary>
internal static class HoldoutAssignmentFunction
{
    private const double TwoToThe64 = 18446744073709551616d;

    /// <summary>
    /// The run's position in [0, 1): SHA-256 over the run ID's RFC 4122 bytes and the salt's UTF-8 bytes, first 8 bytes
    /// as a big-endian unsigned integer, divided by 2^64.
    /// </summary>
    internal static double Fraction(Guid runId, string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var input = new byte[16 + saltBytes.Length];
        if (!runId.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _))
        {
            throw new InvalidOperationException("A run ID is 16 bytes.");
        }

        saltBytes.CopyTo(input, 16);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return BinaryPrimitives.ReadUInt64BigEndian(digest) / TwoToThe64;
    }

    /// <summary>The run's arm at <paramref name="rate"/>: <see cref="HoldoutArm.Holdout"/> when its fraction is below the rate, or the rate is 1.</summary>
    internal static HoldoutArm Assign(Guid runId, string salt, double rate) =>
        rate >= 1 || Fraction(runId, salt) < rate ? HoldoutArm.Holdout : HoldoutArm.Treatment;
}
