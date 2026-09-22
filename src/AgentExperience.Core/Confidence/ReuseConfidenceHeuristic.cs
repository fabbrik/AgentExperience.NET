using System.Globalization;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// The versioned rule that turns a record's evidence counters into its reuse confidence, and the
/// independence keys that decide which submissions are allowed to move those counters. Core owns this
/// arithmetic outright: it reads the record, computes the new counters and score here, and submits them
/// with the revision it read. No adapter derives a score.
/// </summary>
/// <remarks>
/// <para>
/// <b>The score.</b> <c>(1 + S) / (2 + S + F)</c>, where <c>S</c> counts independent accepted
/// supporting validations and <c>F</c> counts independent accepted contradictions. It is Laplace's rule
/// of succession, and it is a <em>heuristic</em>: a monotone, bounded summary of how often reuse held
/// up, suitable for ranking and for a floor. It is not calibrated against anything, so nothing in this
/// library -- or in the documentation of it -- may present it as the probability that the next reuse
/// will succeed.
/// </para>
/// <para>
/// <b>Why the two added terms.</b> They are the rule's prior, and they are what keeps the score honest
/// at the extremes: a record with one supporting validation and nothing else scores 2/3 rather than 1,
/// and a record with nothing but contradictions approaches 0 without ever reaching it. The score is
/// therefore always strictly inside (0, 1), whatever the sequence of evidence -- which is
/// <see cref="Score"/>'s contract, not an accident of the arithmetic.
/// </para>
/// <para>
/// <b>What it is independent of.</b> <see cref="ExperienceRecord.CompletionScore"/> -- the fraction of
/// required checks that passed at finalization -- is a different number about a different question and
/// is never an input here. Neither is <see cref="ExperienceRecord.Status"/>: the score does not depend
/// on it, and it cannot change it. A number never makes an ineligible record eligible; only a status
/// change does, and only <see cref="ExperienceStatus.Validated"/> and
/// <see cref="ExperienceStatus.Reinforced"/> are eligible at all.
/// </para>
/// <para>
/// <b>Independence.</b> Counting the same observation twice would let one run inflate a record without
/// bound, so each accepted submission is keyed and the first submission for a key is the only one that
/// counts (<see cref="IndependenceKeyFor(ConfidenceUpdate)"/>). Machine evidence is keyed on the run
/// and the verification round; human evidence on the reviewer and the run. The key is enforced by the
/// store's unique index, not here: only the transaction that writes the counters can decide who was
/// first.
/// </para>
/// <para>
/// <b>The key's inputs are a host trust boundary.</b> Nothing in this library can check that a run
/// happened or that a verification round was closed, and there is no foreign key behind either: a caller
/// passing a fresh <see cref="Guid"/> for both on every submission gets a fresh key every time and
/// drives the score as high as it likes. The run ID and the verification round ID must therefore be
/// established by the host, from its own run bookkeeping and its own closed rounds, exactly as
/// <see cref="AuthorizationContext"/> is -- never passed through from something an agent produced. What
/// the independence rule guarantees is narrower than it first looks, and worth stating plainly: a host
/// that establishes them honestly cannot have its own observations counted twice.
/// </para>
/// </remarks>
public static class ReuseConfidenceHeuristic
{
    /// <summary>
    /// Identifies the rule version every update computed by this build is produced under, so a future
    /// rule change stays auditable against scores computed by an earlier one. Every accepted update
    /// records it (<see cref="ConfidenceUpdate.RuleVersion"/>).
    /// </summary>
    public const string RuleVersion = "1.0.0";

    /// <summary>
    /// The statuses in which a record may receive confidence evidence at all. It is deliberately
    /// <em>not</em> <see cref="ExperienceStatuses.EligibleForReuse"/>: a
    /// <see cref="ExperienceStatus.Contested"/> record is not reusable but is exactly the record a
    /// further contradiction is about, so evidence keeps accruing against it. Every other status is
    /// refused -- a <see cref="ExperienceStatus.Candidate"/> has not been validated yet, and a
    /// <see cref="ExperienceStatus.Quarantined"/>, <see cref="ExperienceStatus.Stale"/>,
    /// <see cref="ExperienceStatus.Superseded"/>, or <see cref="ExperienceStatus.Revoked"/> record has
    /// been withdrawn, replaced, or aged out by a decision that evidence about reuse does not revisit.
    /// </summary>
    public static IReadOnlyList<ExperienceStatus> AcceptsEvidence { get; } =
        [ExperienceStatus.Validated, ExperienceStatus.Reinforced, ExperienceStatus.Contested];

    /// <summary>Whether a record in <paramref name="status"/> may receive confidence evidence.</summary>
    /// <remarks>
    /// It asks <see cref="AcceptsEvidence"/> rather than restating the three members, so the rule exists
    /// in exactly one place and the list and the question can never come to disagree.
    /// </remarks>
    /// <param name="status">The record's stored status.</param>
    /// <returns><see langword="true"/> when the status is one of <see cref="AcceptsEvidence"/>.</returns>
    public static bool AcceptsEvidenceIn(ExperienceStatus status) => AcceptsEvidence.Contains(status);

    /// <summary>
    /// The reuse confidence for <paramref name="supportingValidations"/> supporting observations and
    /// <paramref name="contradictions"/> contradicting ones: <c>(1 + S) / (2 + S + F)</c>.
    /// </summary>
    /// <remarks>
    /// The result is always strictly inside (0, 1) and is computed in <see cref="double"/>, so it is
    /// exactly as reproducible as IEEE-754 division is -- two callers with the same counters get the
    /// same bits.
    /// </remarks>
    /// <param name="supportingValidations">Independent accepted supporting validations, including the initial validation. Must not be negative.</param>
    /// <param name="contradictions">Independent accepted contradictions. Must not be negative.</param>
    /// <returns>The score, in the open interval (0, 1).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either count is negative.</exception>
    public static double Score(int supportingValidations, int contradictions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(supportingValidations);
        ArgumentOutOfRangeException.ThrowIfNegative(contradictions);

        return (1d + supportingValidations) / (2d + supportingValidations + contradictions);
    }

    /// <summary>
    /// The status a record in <paramref name="currentStatus"/> ends up in once evidence of
    /// <paramref name="kind"/> is accepted against it.
    /// </summary>
    /// <remarks>
    /// A contradiction against a live record moves it to <see cref="ExperienceStatus.Contested"/>, in
    /// the same transaction that records the evidence. Supporting evidence never moves a status by
    /// itself -- reinforcement is a separate, explicitly requested transition -- which is what lets a
    /// record be reinforced repeatedly through its counters even though
    /// <see cref="ExperienceStatus.Validated"/> to <see cref="ExperienceStatus.Reinforced"/> may only
    /// happen once.
    /// </remarks>
    /// <param name="currentStatus">The record's stored status, which must be one of <see cref="AcceptsEvidence"/>.</param>
    /// <param name="kind">The evidence being applied.</param>
    /// <returns>The status the update moves the record to, which may be the one it is already in.</returns>
    public static ExperienceStatus StatusAfter(ExperienceStatus currentStatus, ConfidenceEvidenceKind kind) =>
        kind == ConfidenceEvidenceKind.Contradicting
        && currentStatus is ExperienceStatus.Validated or ExperienceStatus.Reinforced
            ? ExperienceStatus.Contested
            : currentStatus;

    /// <summary>
    /// Computes the update one piece of evidence would make to <paramref name="record"/>, assuming its
    /// independence key is free. Whether it actually was is the store's to decide inside the
    /// transaction that writes it; a store that finds the key taken records this submission with
    /// <see cref="ConfidenceUpdate.AsRecordedOnly"/> instead.
    /// </summary>
    /// <param name="record">The record as Core read it. Its counters and confidence become the update's prior values.</param>
    /// <param name="evidenceId">The submission's identifier.</param>
    /// <param name="kind">Whether the evidence supports reuse or contradicts it.</param>
    /// <param name="source">Whether a machine evaluator or a human reviewer observed it.</param>
    /// <param name="runId">The run the reuse was observed in.</param>
    /// <param name="verificationRoundId">The verification round, for machine evidence; <see langword="null"/> for human evidence.</param>
    /// <param name="reviewerIdentity">The reviewing principal, for human evidence; <see langword="null"/> for machine evidence.</param>
    /// <param name="detail">Optional sanitized detail.</param>
    /// <returns>The update to submit with the record's revision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The record's counters are negative, or applying this evidence would overflow one of them.</exception>
    public static ConfidenceUpdate Apply(
        ExperienceRecord record,
        Guid evidenceId,
        ConfidenceEvidenceKind kind,
        ConfidenceEvidenceSource source,
        Guid runId,
        Guid? verificationRoundId,
        string? reviewerIdentity,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.SupportingValidations, $"{nameof(record)}.{nameof(record.SupportingValidations)}");
        ArgumentOutOfRangeException.ThrowIfNegative(record.Contradictions, $"{nameof(record)}.{nameof(record.Contradictions)}");

        var supporting = record.SupportingValidations;
        var contradictions = record.Contradictions;

        // checked, so a counter at int.MaxValue is a loud failure rather than a silent wrap into the
        // negative counts the record's own contract forbids.
        if (kind == ConfidenceEvidenceKind.Supporting)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(supporting, int.MaxValue, nameof(record));
            supporting++;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfEqual(contradictions, int.MaxValue, nameof(record));
            contradictions++;
        }

        return new ConfidenceUpdate(
            EvidenceId: evidenceId,
            Kind: kind,
            Source: source,
            RunId: runId,
            VerificationRoundId: verificationRoundId,
            ReviewerIdentity: reviewerIdentity,
            RuleVersion: RuleVersion,
            PriorReuseConfidence: record.ReuseConfidence,
            NewReuseConfidence: Score(supporting, contradictions),
            PriorSupportingValidations: record.SupportingValidations,
            NewSupportingValidations: supporting,
            PriorContradictions: record.Contradictions,
            NewContradictions: contradictions,
            Detail: detail);
    }

    /// <summary>
    /// The independence key <paramref name="update"/> is deduplicated on: the record it is about, plus
    /// the observation it came from.
    /// </summary>
    /// <param name="update">The submission.</param>
    /// <returns>The key, whose <see cref="ConfidenceIndependenceKey.Value"/> the store's unique index pins.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The submission does not carry the identifiers its <see cref="ConfidenceUpdate.Source"/> requires.</exception>
    public static ConfidenceIndependenceKey IndependenceKeyFor(ConfidenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return update.Source switch
        {
            ConfidenceEvidenceSource.Machine when update.VerificationRoundId is { } roundId =>
                ConfidenceIndependenceKey.ForMachine(update.RunId, roundId),
            ConfidenceEvidenceSource.Human when !string.IsNullOrWhiteSpace(update.ReviewerIdentity) =>
                ConfidenceIndependenceKey.ForHuman(update.ReviewerIdentity, update.RunId),
            _ => throw new ArgumentException(
                "Machine evidence needs a verification round and human evidence needs a reviewer identity; " +
                "without one there is no key to count it independently under.",
                nameof(update)),
        };
    }
}

/// <summary>
/// The key an accepted confidence submission is counted under, within one Experience Record. The first
/// submission for a key moves the counters; every later one is stored for audit and counted zero times.
/// </summary>
/// <remarks>
/// <para>
/// The record is deliberately not part of <see cref="Value"/>: the store's unique index is on the
/// record column and this string together, so the record stays a first-class column that a scoped query
/// can filter on rather than being buried inside an opaque key.
/// </para>
/// <para>
/// <see cref="Value"/> is a stable, ordinal string, with its GUIDs in PostgreSQL's own lower-case
/// <c>D</c> form, because the database computes the same string from the same columns -- the rule is
/// deliberately stated twice, here and in migration <c>0007</c>, and pinned by a test, so neither side
/// can drift into counting an observation the other would have deduplicated. A reviewer identity is
/// carried through verbatim and compared ordinally; nothing here folds case.
/// </para>
/// </remarks>
/// <param name="Source">Which keying rule produced <paramref name="Value"/>.</param>
/// <param name="Value">The key itself.</param>
public readonly record struct ConfidenceIndependenceKey(ConfidenceEvidenceSource Source, string Value)
{
    /// <summary>
    /// The key for a machine observation: one verification round of one run counts once, however many
    /// evaluators report it and however many times it is resubmitted.
    /// </summary>
    /// <param name="runId">The run the reuse was observed in.</param>
    /// <param name="verificationRoundId">The verification round the observation came from.</param>
    /// <returns>The key.</returns>
    public static ConfidenceIndependenceKey ForMachine(Guid runId, Guid verificationRoundId) => new(
        ConfidenceEvidenceSource.Machine,
        string.Create(CultureInfo.InvariantCulture, $"machine:{runId:D}:{verificationRoundId:D}"));

    /// <summary>
    /// The key for a human judgement: one reviewer's opinion about one run counts once. The reviewer is
    /// the host's <see cref="AuthorizationContext.PrincipalId"/>, so two agents cannot manufacture two
    /// independent reviewers out of one principal.
    /// </summary>
    /// <remarks>
    /// The identity is used exactly as the host established it and compared ordinally and
    /// case-sensitively, like every other identity in this library. It is deliberately not folded or
    /// trimmed: a host-assigned principal is opaque, and guessing that two spellings mean one person
    /// would be this library deciding who a reviewer is. A value with leading or trailing whitespace is
    /// refused rather than quietly normalized, so the one difference a caller cannot see is not the one
    /// that silently creates a second reviewer.
    /// </remarks>
    /// <param name="reviewerIdentity">The reviewing principal.</param>
    /// <param name="runId">The run the reuse was observed in.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException"><paramref name="reviewerIdentity"/> is blank, or has leading or trailing whitespace.</exception>
    public static ConfidenceIndependenceKey ForHuman(string reviewerIdentity, Guid runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerIdentity);
        if (!string.Equals(reviewerIdentity, reviewerIdentity.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A reviewer identity may not have leading or trailing whitespace: it would key as a second, independent reviewer.",
                nameof(reviewerIdentity));
        }

        return new(
            ConfidenceEvidenceSource.Human,
            string.Create(CultureInfo.InvariantCulture, $"human:{reviewerIdentity}:{runId:D}"));
    }
}
