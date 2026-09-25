using AgentExperience.Abstractions;
using Microsoft.Agents.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The bounds one <see cref="AgentSession"/> runs under across all of its invocations: how many records,
/// and how many UTF-8 bytes of Historical Reference, the session may be given in total. Set through
/// <see cref="ExperienceInjectionOptions.SessionLimits"/>; <see langword="null"/> there turns session
/// tracking off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a session needs its own budget.</b> <see cref="ExperienceInjectionLimits"/> bounds one injected
/// block. MAF's <see cref="ChatClientAgent"/> keeps an invocation's request messages, the injected block
/// included, in the session's chat history, so every later turn of that session shows the model every
/// earlier block too. This budget is what bounds the conversation.
/// </para>
/// <para>
/// <b>What is charged.</b> Each record injected costs one record, every time it is injected (a newer
/// revision of a record already delivered is a new delivery). Each injected block costs its full UTF-8
/// size, header, footer and retraction notices included. A delivery is charged only once MAF reports the
/// invocation succeeded; see <see cref="ExperienceContextProvider"/>.
/// </para>
/// <para>
/// <b>Retractions are charged but never refused.</b> A notice that a record delivered earlier in the
/// session has been withdrawn is written whatever the session budget says, because withdrawal is the
/// safety property. The overshoot is bounded: each delivery is withdrawn at most once, a record withdrawn
/// and delivered again costs another delivery, and deliveries are capped by <see cref="MaxRecords"/>, so
/// notices alone can take a session past <see cref="MaxBytes"/> by at most that many notice lines, plus
/// one block's framing for each invocation whose block carried notices and no record.
/// </para>
/// <para>
/// Both values are validated at construction and on a <c>with</c> expression, as
/// <see cref="ExperienceInjectionLimits"/> is.
/// </para>
/// </remarks>
/// <param name="MaxRecords">
/// The most record deliveries one session may receive. Must be strictly positive and at most
/// <see cref="MaxTrackedRecords"/>, because every record the session holds is re-checked in one batched
/// read on every invocation.
/// </param>
/// <param name="MaxBytes">The most UTF-8 bytes of Historical Reference one session may receive, retraction notices aside. Must be strictly positive.</param>
public sealed record ExperienceInjectionSessionLimits(int MaxRecords, int MaxBytes)
{
    /// <summary>The documented default session record budget: 32 record deliveries.</summary>
    public const int DefaultMaxRecords = 32;

    /// <summary>The documented default session byte budget: 64 KB of UTF-8, four default-sized blocks.</summary>
    public const int DefaultMaxBytes = 64 * 1024;

    /// <summary>
    /// The largest permitted <see cref="MaxRecords"/>, and the most entries a session's tracking state may
    /// hold: <see cref="ExperienceRecordGetManyResult.MaxCount"/>, so every record a session holds fits in
    /// the one batched read that re-checks them.
    /// </summary>
    public const int MaxTrackedRecords = ExperienceRecordGetManyResult.MaxCount;

    /// <summary>The documented defaults: 32 record deliveries and 64 KB of UTF-8 per session.</summary>
    public static ExperienceInjectionSessionLimits Default { get; } = new(DefaultMaxRecords, DefaultMaxBytes);

    /// <summary>The most record deliveries one session may receive (see the primary constructor's parameter doc).</summary>
    public int MaxRecords
    {
        get;
        init => field = EnsureRecords(value);
    } = EnsureRecords(MaxRecords);

    /// <summary>The most UTF-8 bytes one session may receive (see the primary constructor's parameter doc).</summary>
    public int MaxBytes
    {
        get;
        init => field = EnsureBytes(value);
    } = EnsureBytes(MaxBytes);

    private static int EnsureRecords(int value) =>
        value is > 0 and <= MaxTrackedRecords
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxRecords),
                value,
                $"The session record budget must be strictly positive and at most {MaxTrackedRecords}.");

    private static int EnsureBytes(int value) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxBytes), value, "The session byte budget must be strictly positive.");
}
