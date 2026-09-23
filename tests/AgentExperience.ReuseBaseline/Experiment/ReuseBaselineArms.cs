using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Experiment;

/// <summary>
/// The three pre-registered arms and the versioned task sets they run.
/// </summary>
/// <remarks>
/// <para>
/// The reference arm and the negative control share an evaluation set, an agent policy, a gate and a
/// trial count. Only the learning tasks differ. That is what makes the negative control a control
/// rather than a different experiment: the one thing that changes is whether the experience the
/// agent is handed is worth anything to it.
/// </para>
/// <para>
/// <b>The evaluation tasks are not the learning tasks reworded.</b> They describe the same two
/// failure modes -- a write blocked behind something that will not let go of what it holds, and a
/// service shedding work it cannot absorb while its backlog grows -- in a different system, with a
/// different vocabulary. An earlier version of this file did not: its evaluation tasks repeated the
/// learning tasks almost word for word, which made the memory-enabled arm's advantage a lookup
/// rather than a generalisation. <see cref="ReuseBaselineTaskSet.Validate"/> now measures the
/// wording overlap and refuses a task set that repeats one.
/// </para>
/// <para>
/// Every task's resolving strategy is declared here in the open. A reader can compute the
/// memory-disabled arm's failed_attempts by hand from this file alone -- it is the position of the
/// resolving strategy in <see cref="IncidentStrategies.ExplorationOrder"/> -- which is the point:
/// the number is arithmetic over a fixture, and the report says so rather than presenting it as a
/// finding.
/// </para>
/// </remarks>
public static class ReuseBaselineArms
{
    /// <summary>
    /// The six held-out evaluation tasks the reference arm and the negative control share. Their ids
    /// appear in no learning set, and neither does their wording.
    /// </summary>
    /// <remarks>
    /// Three are the lock-contention failure mode -- something holds what the writer needs and is
    /// not coming back -- which <c>wait-for-lock</c> resolves. Three are the saturation failure mode
    /// -- more work arriving than the service can absorb, with the backlog growing -- which
    /// <c>escalate-to-oncall</c> resolves. An agent that generalises from the learning set can reach
    /// them; an agent that pattern-matches on the sentence cannot.
    /// </remarks>
    private static readonly ReuseBaselineTask[] EvaluationTasks =
    [
        new("eval-incident-101",
            "Payroll export cannot commit; an abandoned connection is sitting on the account balance it needs to change.",
            IncidentStrategies.WaitForLock),
        new("eval-incident-102",
            "Nobody can update the customer wallet: an orphaned transaction has kept an exclusive claim on it since midnight.",
            IncidentStrategies.WaitForLock),
        new("eval-incident-103",
            "The invoice writer waits forever behind a client that opened a change and then disappeared.",
            IncidentStrategies.WaitForLock),
        new("eval-incident-201",
            "Order intake is turning away traffic it cannot absorb and the backlog behind it grows every minute.",
            IncidentStrategies.EscalateToOnCall),
        new("eval-incident-202",
            "The payments API answers most callers with 503 and its pending work has doubled since midnight.",
            IncidentStrategies.EscalateToOnCall),
        new("eval-incident-203",
            "Checkout has run out of headroom, rejects arriving work, and nothing is draining what piled up.",
            IncidentStrategies.EscalateToOnCall),
    ];

    /// <summary>
    /// The reference arm. Its learning tasks are resolved by the two strategies that sit
    /// <em>last</em> in the exploration order, so an injected lesson names something the exploring
    /// agent would not have reached first.
    /// </summary>
    public static ExperimentArm Reference { get; } = new(
        "reference",
        "the reference experiment: injected records name approaches the exploring agent would not have reached first",
        new ReuseBaselineTaskSet(
            "reuse-baseline-incidents@2",
            IncidentStrategies.ExplorationOrder,
            [
                new("learn-settlement-batch-stalled",
                    "A settlement batch has stalled because the ledger row it writes is held by a stale session.",
                    IncidentStrategies.WaitForLock),
                new("learn-settlement-gateway-shedding",
                    "The settlement gateway is shedding requests under backpressure while queue depth climbs.",
                    IncidentStrategies.EscalateToOnCall),
            ],
            EvaluationTasks));

    /// <summary>
    /// The negative control, and the required deliverable of frozen rule 6. Its learning tasks are
    /// resolved by the two strategies that sit <em>first</em> in the exploration order, so the
    /// candidate list an injected lesson produces is the exploration order itself and the injected
    /// experience carries no usable advantage at all.
    /// </summary>
    /// <remarks>
    /// This holds however the two records happen to rank, and whether one or both are injected: any
    /// prefix of <c>[retry-immediately, rebuild-index]</c> in any order, followed by the exploration
    /// order with those removed, resolves every evaluation task at exactly the same attempt as the
    /// exploration order alone. The control is therefore robust to the ranking rather than tuned to
    /// it.
    /// </remarks>
    public static ExperimentArm NegativeControl { get; } = new(
        "negative-control",
        "the negative control: injected records name approaches the exploring agent would have tried first anyway",
        new ReuseBaselineTaskSet(
            "reuse-baseline-incidents-negative-control@2",
            IncidentStrategies.ExplorationOrder,
            [
                new("learn-settlement-batch-connector-flap",
                    "A settlement batch left a stale ledger row after the settlement connector flapped once.",
                    IncidentStrategies.RetryImmediately),
                new("learn-settlement-gateway-projection-drift",
                    "The settlement gateway queue depth and the settlement projection disagree after a drift.",
                    IncidentStrategies.RebuildIndex),
            ],
            EvaluationTasks));

    /// <summary>
    /// The wrong-strategy arm: the injected record names an approach that does not resolve any of
    /// this arm's evaluation tasks, and sits <em>last</em> in the exploration order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is for.</b> A harness that quietly handed the agent the answer rather than the
    /// injected block would pass the reference arm and the negative control alike, and would look
    /// exactly like this one does not. Here the block can only make things worse: its one strategy,
    /// <c>escalate-to-oncall</c>, goes to the front of a candidate list whose every evaluation task
    /// is resolved by <c>wait-for-lock</c>, so the memory-enabled condition must cost
    /// <em>exactly one more</em> failed attempt than the memory-disabled one -- 3 against 2, every
    /// trial. An agent reading anything other than the block cannot produce that number.
    /// </para>
    /// <para>
    /// One learning task, not two, so the block names exactly one strategy and the arithmetic has a
    /// single answer rather than a ranking-dependent one. Its evaluation tasks are all the
    /// lock-contention failure mode for the same reason.
    /// </para>
    /// </remarks>
    public static ExperimentArm WrongStrategy { get; } = new(
        "wrong-strategy",
        "the wrong-strategy arm: the injected record names an approach that resolves none of these tasks, so the block must cost exactly one extra attempt",
        new ReuseBaselineTaskSet(
            "reuse-baseline-incidents-wrong-strategy@1",
            IncidentStrategies.ExplorationOrder,
            [
                new("learn-settlement-gateway-shedding",
                    "The settlement gateway is shedding requests under backpressure while queue depth climbs.",
                    IncidentStrategies.EscalateToOnCall),
            ],
            [
                EvaluationTasks[0],
                EvaluationTasks[1],
                EvaluationTasks[2],
                new("eval-incident-104",
                    "The refund job has been waiting all night for a lock a disconnected worker never gave back.",
                    IncidentStrategies.WaitForLock),
                new("eval-incident-105",
                    "An export makes no progress: another process took the entry first and never released it.",
                    IncidentStrategies.WaitForLock),
                new("eval-incident-106",
                    "Statement generation hangs behind a crashed job whose claim on the shared entry is still in place.",
                    IncidentStrategies.WaitForLock),
            ]));

    /// <summary>Every pre-registered arm, in the order the pre-registration declares them.</summary>
    public static IReadOnlyList<ExperimentArm> All { get; } = [Reference, NegativeControl, WrongStrategy];
}
