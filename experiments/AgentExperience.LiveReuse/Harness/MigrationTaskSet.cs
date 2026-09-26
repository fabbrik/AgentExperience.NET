using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The rollout strategies <c>apply_migration</c> accepts, in the order its tool description lists them.
/// </summary>
/// <remarks>
/// Every service's database accepts exactly one of them, and which one is a property of that database that nothing
/// the agent can read reveals: not the task text, not <c>describe_service</c>, not the error a rejected strategy
/// returns. That is what makes a verified past run genuinely useful and not reconstructible from first principles.
/// </remarks>
public static class RolloutStrategies
{
    public const string InPlace = "in-place";
    public const string OnlineCopy = "online-copy";
    public const string ExpandContract = "expand-contract";
    public const string BlueGreen = "blue-green";
    public const string ShadowTable = "shadow-table";
    public const string BatchedBackfill = "batched-backfill";

    /// <summary>The listing order in the tool description. Fixed, and identical in every condition.</summary>
    public static IReadOnlyList<string> All { get; } = [InPlace, OnlineCopy, ExpandContract, BlueGreen, ShadowTable, BatchedBackfill];

    /// <summary>The first strategy named in <paramref name="text"/>, by position, or <see langword="null"/>.</summary>
    public static string? FirstNamedIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return All
            .Select(strategy => (Strategy: strategy, At: IndexOfWord(text, strategy)))
            .Where(found => found.At >= 0)
            .OrderBy(found => found.At)
            .Select(found => found.Strategy)
            .FirstOrDefault();
    }

    // "in-place" must not match inside another token; no strategy name is a substring of another, but a
    // word boundary keeps it that way if one is ever added.
    private static int IndexOfWord(string text, string word)
    {
        var from = 0;
        while (from < text.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            var before = at == 0 || !IsWordChar(text[at - 1]);
            var afterIndex = at + word.Length;
            var after = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
            if (before && after)
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}

/// <summary>What <c>describe_service</c> reports. Chosen by hand by an author who knew the assignment; a test checks the facts do not pair up with it.</summary>
public sealed record ServiceFacts(string Engine, int RowsMillions, int PeakWritesPerSecond, int ReadReplicas);

/// <summary>
/// One task instance: a service, the strategy its database accepts, the stale strategy the negative control's
/// record names, and two different migrations -- one the learning run applies, one the evaluation trials apply.
/// </summary>
public sealed record MigrationInstance(
    int Index,
    string Service,
    string HiddenStrategy,
    string StaleStrategy,
    string LearningMigration,
    string LearningDescription,
    string EvaluationMigration,
    string EvaluationDescription,
    ServiceFacts Facts)
{
    /// <summary>The learning run's task text. Names the service and the migration, never a strategy.</summary>
    public string LearningText => string.Format(
        CultureInfo.InvariantCulture,
        "Apply migration `{0}` to the `{1}` production database. The migration {2}. It has been reviewed and approved; get it applied.",
        LearningMigration,
        Service,
        LearningDescription);

    /// <summary>
    /// The evaluation trials' task text: another migration, in other words, on the same service. Identical in all
    /// four conditions; the conditions differ only in what, if anything, is injected.
    /// </summary>
    public string EvaluationText => string.Format(
        CultureInfo.InvariantCulture,
        "Change ticket: migration `{0}` needs to go live on `{1}`. It {2}. Roll it out to production and make sure it is live.",
        EvaluationMigration,
        Service,
        EvaluationDescription);
}

/// <summary>Why a task set cannot be run.</summary>
public sealed class TaskSetException(string message) : Exception(message);

/// <summary>
/// The twelve task instances. Versioned, and locked by <see cref="AssignmentDigest"/>, which the pre-registration
/// records: changing which strategy any service accepts changes the digest and the harness refuses to run.
/// </summary>
public sealed class MigrationTaskSet
{
    public const string CurrentVersion = "live-reuse-migrations@1";

    // Declared before Current: static initializers run in textual order, and Build() reads this.
    private static readonly int[] StaleOffsets = [1, 5, 5, 2, 3, 3, 3, 2, 2, 5, 4, 1];

    private MigrationTaskSet(string version, IReadOnlyList<MigrationInstance> instances)
    {
        Version = version;
        Instances = instances;
    }

    public string Version { get; }

    public IReadOnlyList<MigrationInstance> Instances { get; }

    /// <summary>The task set the pre-registration names.</summary>
    public static MigrationTaskSet Current { get; } = Build();

    /// <summary>
    /// A task set with other instances, for the harness's own tests (a smaller set, a planted defect). Never used by
    /// the live run, which always runs <see cref="Current"/> and checks it against the pre-registration.
    /// </summary>
    internal static MigrationTaskSet ForTests(string version, IReadOnlyList<MigrationInstance> instances) => new(version, instances);

    /// <summary>
    /// SHA-256 over every instance's service, hidden strategy and stale strategy, in index order.
    /// </summary>
    public string AssignmentDigest()
    {
        var text = new StringBuilder();
        foreach (var instance in Instances)
        {
            text.Append(instance.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(instance.Service).Append('|')
                .Append(instance.HiddenStrategy).Append('|')
                .Append(instance.StaleStrategy).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Refuses a task set that could leak its answers or make the comparison meaningless.
    /// </summary>
    /// <exception cref="TaskSetException">The first problem found.</exception>
    public void Validate()
    {
        if (Instances.Count == 0)
        {
            throw new TaskSetException("The task set has no instances.");
        }

        for (var index = 0; index < Instances.Count; index++)
        {
            var instance = Instances[index];
            if (instance.Index != index)
            {
                throw new TaskSetException($"Instance at position {index} carries index {instance.Index}.");
            }

            if (!RolloutStrategies.All.Contains(instance.HiddenStrategy) || !RolloutStrategies.All.Contains(instance.StaleStrategy))
            {
                throw new TaskSetException($"Instance {index} names a strategy apply_migration does not accept.");
            }

            // The negative control's record must be wrong for the evaluation trial, or it is not a negative control.
            if (instance.StaleStrategy == instance.HiddenStrategy)
            {
                throw new TaskSetException($"Instance {index}: the stale strategy equals the hidden one, so the negative control's record would be right.");
            }

            // Nothing the agent reads may name a strategy: not either task text, not describe_service.
            foreach (var (what, text) in new[]
            {
                ("learning text", instance.LearningText),
                ("evaluation text", instance.EvaluationText),
                ("describe_service output", MigrationEnvironment.Describe(instance)),
            })
            {
                if (RolloutStrategies.FirstNamedIn(text) is { } named)
                {
                    throw new TaskSetException($"Instance {index}: the {what} names the strategy '{named}'.");
                }
            }

            // The evaluation task is a different migration, in different words, on the same service.
            if (instance.LearningMigration == instance.EvaluationMigration || instance.LearningDescription == instance.EvaluationDescription)
            {
                throw new TaskSetException($"Instance {index}: the evaluation task repeats the learning task's migration.");
            }
        }

        if (Instances.Select(instance => instance.Service).Distinct(StringComparer.Ordinal).Count() != Instances.Count)
        {
            throw new TaskSetException("Two instances share a service.");
        }

        // Two instances with the same hidden AND stale strategy would be near-duplicate observations for a
        // deterministic model, and the sign test would count them as independent evidence.
        if (Instances.Select(instance => (instance.HiddenStrategy, instance.StaleStrategy)).Distinct().Count() != Instances.Count)
        {
            throw new TaskSetException("Two instances share both their hidden and their stale strategy.");
        }

        // Balanced: every strategy is the answer equally often, so a model's fixed preference order cannot favour
        // the answers by construction, and no single answer is worth memorizing.
        if (Instances.Count % RolloutStrategies.All.Count == 0)
        {
            var expected = Instances.Count / RolloutStrategies.All.Count;
            var unbalanced = RolloutStrategies.All.Where(strategy => Instances.Count(instance => instance.HiddenStrategy == strategy) != expected).ToList();
            if (unbalanced.Count > 0)
            {
                throw new TaskSetException($"The hidden strategies are unbalanced: [{string.Join(", ", unbalanced)}] are not each the answer {expected} time(s).");
            }
        }
    }

    private static MigrationTaskSet Build()
    {
        // (service, learning migration, learning description, evaluation migration, evaluation description, facts)
        var rows = new (string Service, string LearnId, string Learn, string EvalId, string Eval, ServiceFacts Facts)[]
        {
            ("orders-api", "2026_07_orders_add_fulfilment_index", "adds a composite index on orders(fulfilment_center_id, created_at)", "2026_09_orders_add_gift_note", "adds a nullable gift_note column to orders", new ServiceFacts("PostgreSQL 16", 420, 1800, 2)),
            ("billing-ledger", "2026_07_billing_add_currency_column", "adds a currency column with a default to ledger_entries", "2026_09_billing_add_posted_index", "adds an index on ledger_entries(posted_at)", new ServiceFacts("PostgreSQL 17", 95, 600, 1)),
            ("inventory-sync", "2026_07_inventory_widen_sku", "widens stock_levels.sku from varchar(32) to varchar(64)", "2026_09_inventory_add_reservation_ttl", "adds a reservation_expires_at column to reservations", new ServiceFacts("PostgreSQL 16", 60, 2400, 3)),
            ("customer-profile", "2026_07_profile_add_locale", "adds a preferred_locale column to customers", "2026_09_profile_add_email_check", "adds a check constraint on customers.email", new ServiceFacts("PostgreSQL 16", 38, 150, 2)),
            ("shipment-tracker", "2026_07_shipments_add_event_index", "adds an index on tracking_events(shipment_id, occurred_at)", "2026_09_shipments_add_carrier_ref", "adds a carrier_reference column to shipments", new ServiceFacts("PostgreSQL 17", 910, 3200, 1)),
            ("payments-gateway", "2026_07_payments_add_idempotency_key", "adds a unique idempotency_key column to payment_intents", "2026_09_payments_add_captured_at", "adds a captured_at timestamp column to payment_intents", new ServiceFacts("PostgreSQL 17", 210, 950, 3)),
            ("catalog-search", "2026_07_catalog_add_search_vector", "adds a generated search_vector column to products", "2026_09_catalog_widen_title", "widens products.title from varchar(120) to varchar(255)", new ServiceFacts("PostgreSQL 16", 12, 80, 1)),
            ("returns-portal", "2026_07_returns_add_reason_code", "adds a reason_code column to return_requests", "2026_09_returns_add_label_index", "adds an index on return_requests(label_id)", new ServiceFacts("PostgreSQL 17", 7, 45, 3)),
            ("loyalty-points", "2026_07_loyalty_add_expiry_index", "adds an index on point_grants(expires_at)", "2026_09_loyalty_add_tier_column", "adds a tier column with a default to members", new ServiceFacts("PostgreSQL 16", 150, 700, 1)),
            ("notifications-hub", "2026_07_notifications_add_channel_column", "adds a channel column to notifications", "2026_09_notifications_add_sent_index", "adds an index on notifications(sent_at)", new ServiceFacts("PostgreSQL 17", 1300, 4100, 2)),
            ("pricing-engine", "2026_07_pricing_add_region_column", "adds a region column to price_rules", "2026_09_pricing_widen_amounts", "widens price_rules.amount from numeric(10,2) to numeric(14,4)", new ServiceFacts("PostgreSQL 16", 25, 300, 3)),
            ("fraud-scoring", "2026_07_fraud_add_model_version", "adds a model_version column to risk_scores", "2026_09_fraud_add_device_index", "adds an index on device_signals(device_id)", new ServiceFacts("PostgreSQL 17", 540, 2900, 2)),
        };

        var strategies = RolloutStrategies.All;
        var instances = new List<MigrationInstance>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];

            // Each strategy is the answer exactly twice, in an order that is not the listing order. The stale strategy
            // is always a different one, also each exactly twice, at an offset drawn once with seed 20260926 so that
            // no two instances share both their hidden and their stale strategy (the pair with the same hidden
            // strategy would otherwise be a near-duplicate observation for a deterministic model).
            var hidden = (5 * index + 1) % strategies.Count;
            var stale = (hidden + StaleOffsets[index]) % strategies.Count;

            instances.Add(new MigrationInstance(
                index,
                row.Service,
                strategies[hidden],
                strategies[stale],
                row.LearnId,
                row.Learn,
                row.EvalId,
                row.Eval,
                row.Facts));
        }

        return new MigrationTaskSet(CurrentVersion, instances);
    }
}
