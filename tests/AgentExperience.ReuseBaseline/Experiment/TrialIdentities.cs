using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.ReuseBaseline.Experiment;

/// <summary>
/// Every identifier one trial needs, derived from the arm, the trial index, and what the identifier
/// is for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the 4.2 sample's counter.</b> <c>DeterministicIds</c> restarts at one for every
/// container it is registered in, and the harness builds a fresh container per trial -- so twelve
/// trials would issue twelve identical run identifiers and the whole experiment would be one trial
/// replayed. Deriving from the index instead makes collisions impossible by construction while
/// keeping the run reproducible, which is what the golden report needs.
/// </para>
/// <para>
/// Derivation is a SHA-256 over the arm, the index, the purpose, and an ordinal, shaped into a
/// version-4 GUID. It is an identity scheme for a fixture, never a security claim, and every
/// identifier it produces stays inside this process.
/// </para>
/// </remarks>
internal sealed class TrialIdentities
{
    private readonly string _arm;
    private readonly int _index;
    private int _issued;

    public TrialIdentities(string arm, int index)
    {
        _arm = arm;
        _index = index;

        RunId = Derive(arm, index, "run", 0);
        OpenRoundId = Derive(arm, index, "open-round", 0);
        ClosedRoundId = Derive(arm, index, "closed-round", 0);
        FeedbackId = Derive(arm, index, "feedback", 0);
    }

    /// <summary>The trial's own Experience Run.</summary>
    public Guid RunId { get; }

    /// <summary>
    /// The round the trial's failing attempts' evidence sits in, which the host never closes. Inside
    /// one round a Fail on a required check dominates a later Pass, so the failures and the eventual
    /// success cannot share a round.
    /// </summary>
    public Guid OpenRoundId { get; }

    /// <summary>The trial's own closed verification round. No two trials share one.</summary>
    public Guid ClosedRoundId { get; }

    /// <summary>The trial's own reuse-feedback submission identity.</summary>
    public Guid FeedbackId { get; }

    /// <summary>The next identifier in this trial's own sequence, for attempts, tool calls, and evidence.</summary>
    public Guid Next() => Derive(_arm, _index, "sequence", Interlocked.Increment(ref _issued));

    /// <summary>The identifier <paramref name="purpose"/> number <paramref name="ordinal"/> of one trial.</summary>
    /// <param name="arm">The experiment arm.</param>
    /// <param name="index">The trial's index in the plan.</param>
    /// <param name="purpose">What the identifier is for.</param>
    /// <param name="ordinal">Which one, within that purpose.</param>
    public static Guid Derive(string arm, int index, string purpose, int ordinal)
    {
        var material = string.Format(
            CultureInfo.InvariantCulture,
            "AgentExperience.ReuseBaseline|{0}|{1}|{2}|{3}",
            arm,
            index,
            purpose,
            ordinal);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var bytes = hash.AsSpan(0, 16).ToArray();

        // Shaped as a version-4 variant-1 GUID so it is a well-formed identifier everywhere it is
        // stored, and so it can never come out as Guid.Empty.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
