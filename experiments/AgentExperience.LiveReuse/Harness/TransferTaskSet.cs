using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// A service in the transfer experiment whose database proxy takes migrations: a learning service (one per cluster) or
/// an unseen evaluation service (two per cluster).
/// </summary>
/// <param name="Index">The cluster index for a learning service; the instance index for an evaluation service.</param>
/// <param name="Service">The service name. No word of it is a cluster name or a strategy.</param>
/// <param name="Cluster">The cluster the service's database proxy runs in.</param>
/// <param name="Migration">The migration its task applies.</param>
/// <param name="Description">What the migration does, in the task text.</param>
/// <param name="Facts">What <c>describe_service</c> reports besides the cluster.</param>
public sealed record TransferService(int Index, string Service, string Cluster, string Migration, string Description, ServiceFacts Facts);

/// <summary>The two distractor task families.</summary>
public enum DistractorFamily
{
    /// <summary>Flushing a service's read cache: <c>flush_read_cache</c>.</summary>
    CacheFlush,

    /// <summary>Rolling out a configuration change: <c>push_config</c>, which takes no strategy.</summary>
    ConfigRollout,
}

/// <summary>
/// A distractor: a task from a family other than migrations, on a service in a named cluster. A config rollout carries
/// a change identifier and what the change does; a cache flush carries neither.
/// </summary>
public sealed record TransferDistractor(int Index, string Service, string Cluster, DistractorFamily Family = DistractorFamily.CacheFlush, string? Change = null, string? Description = null)
{
    /// <summary>The distractor's task text, which names its service and its cluster and no strategy.</summary>
    public string Text => Family == DistractorFamily.ConfigRollout
        ? string.Format(
            CultureInfo.InvariantCulture,
            "Roll out config change `{0}` on `{1}` (cluster `{2}`). It {3}. Make sure it is live in production.",
            Change,
            Service,
            Cluster,
            Description)
        : string.Format(
            CultureInfo.InvariantCulture,
            "Flush the read cache on `{0}` in production (cluster `{1}`): the nightly catalog import left stale entries behind.",
            Service,
            Cluster);
}

/// <summary>
/// The transfer experiment's task set: six clusters, each of whose database proxies accepts exactly one rollout
/// strategy (a bijection onto <see cref="RolloutStrategies.All"/>, the hidden trait); one learning service and two
/// unseen evaluation services per cluster; and, per cluster, two distractors from each of two other task families
/// (cache flushes and config rollouts).
/// Versioned, and locked by <see cref="TraitDigest"/>, which <c>preregistration.transfer.json</c> records.
/// </summary>
/// <remarks>
/// The task texts were written once, before the first offline run, and are never edited to improve a retrieval rank:
/// where the library's retrieval does not put the same-cluster record first, the report says so.
/// </remarks>
public sealed class TransferTaskSet
{
    public const string CurrentVersion = "transfer-clusters@1";

    private TransferTaskSet(
        string version,
        IReadOnlyList<string> clusters,
        IReadOnlyDictionary<string, string> strategyByCluster,
        IReadOnlyList<TransferService> learning,
        IReadOnlyList<TransferService> evaluation,
        IReadOnlyList<TransferDistractor> distractors)
    {
        Version = version;
        Clusters = clusters;
        StrategyByCluster = strategyByCluster;
        LearningServices = learning;
        EvaluationInstances = evaluation;
        Distractors = distractors;
    }

    public string Version { get; }

    /// <summary>The clusters, in index order.</summary>
    public IReadOnlyList<string> Clusters { get; }

    /// <summary>The hidden trait: the one strategy every database proxy in a cluster accepts.</summary>
    public IReadOnlyDictionary<string, string> StrategyByCluster { get; }

    /// <summary>One learning service per cluster, in cluster order.</summary>
    public IReadOnlyList<TransferService> LearningServices { get; }

    /// <summary>The twelve evaluation instances: unseen services, two per cluster.</summary>
    public IReadOnlyList<TransferService> EvaluationInstances { get; }

    /// <summary>The twenty-four distractor tasks: two per cluster in each of the two families.</summary>
    public IReadOnlyList<TransferDistractor> Distractors { get; }

    /// <summary>The task set the pre-registration names.</summary>
    public static TransferTaskSet Current { get; } = Build();

    /// <summary>A task set with other contents, for the harness's own tests. Never used by a live run.</summary>
    internal static TransferTaskSet ForTests(
        string version,
        IReadOnlyList<string> clusters,
        IReadOnlyDictionary<string, string> strategyByCluster,
        IReadOnlyList<TransferService> learning,
        IReadOnlyList<TransferService> evaluation,
        IReadOnlyList<TransferDistractor> distractors) =>
        new(version, clusters, strategyByCluster, learning, evaluation, distractors);

    /// <summary>The strategy <paramref name="service"/>'s database proxy accepts: its cluster's.</summary>
    public string HiddenStrategy(TransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return StrategyByCluster[service.Cluster];
    }

    /// <summary>The learning service of <paramref name="cluster"/>.</summary>
    public TransferService LearningServiceFor(string cluster) => LearningServices.Single(service => service.Cluster == cluster);

    /// <summary>The learning run's task text.</summary>
    public static string LearningText(TransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Apply migration `{0}` to the `{1}` production database (cluster `{2}`). The migration {3}. It has been reviewed and approved; get it applied.",
            service.Migration,
            service.Service,
            service.Cluster,
            service.Description);
    }

    /// <summary>The evaluation trials' task text: other words, on a service no learning run touched. Identical in all four conditions.</summary>
    public static string EvaluationText(TransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Change ticket: roll out migration `{0}` on `{1}` (cluster `{2}`). It {3}. Make sure it is live in production.",
            service.Migration,
            service.Service,
            service.Cluster,
            service.Description);
    }

    /// <summary>
    /// The service as the reuse experiment's environment takes it. The stale strategy is not used by the transfer
    /// experiment and is set to the hidden one.
    /// </summary>
    public MigrationInstance ToMigrationInstance(TransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var hidden = HiddenStrategy(service);
        return new MigrationInstance(service.Index, service.Service, hidden, hidden, service.Migration, service.Description, service.Migration, service.Description, service.Facts);
    }

    /// <summary>What <c>describe_service</c> returns for <paramref name="service"/>: the facts, then the cluster.</summary>
    public string Describe(TransferService service) => MigrationEnvironment.Describe(ToMigrationInstance(service), service.Cluster);

    /// <summary>
    /// SHA-256 over the cluster-to-strategy mapping and every service's role, index, name and cluster, in order.
    /// </summary>
    public string TraitDigest()
    {
        var text = new StringBuilder();
        for (var index = 0; index < Clusters.Count; index++)
        {
            var cluster = Clusters[index];
            text.Append("cluster|").Append(index.ToString(CultureInfo.InvariantCulture)).Append('|').Append(cluster).Append('|')
                .Append(StrategyByCluster.TryGetValue(cluster, out var strategy) ? strategy : "?").Append('\n');
        }

        foreach (var (role, services) in new[] { ("learning", LearningServices), ("evaluation", EvaluationInstances) })
        {
            foreach (var service in services)
            {
                text.Append(role).Append('|').Append(service.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(service.Service).Append('|').Append(service.Cluster).Append('\n');
            }
        }

        foreach (var distractor in Distractors)
        {
            text.Append("distractor|").Append(distractor.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(distractor.Service).Append('|').Append(distractor.Cluster).Append('|').Append(distractor.Family.ToString()).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// SHA-256 over every text the agent or the retrieval reads: each learning, evaluation and distractor task text and
    /// each <c>describe_service</c> output, in order, one per line. The trait digest does not lock the wording; this does.
    /// </summary>
    public string TaskTextDigest()
    {
        var text = new StringBuilder();
        foreach (var service in LearningServices)
        {
            text.Append("learning|").Append(LearningText(service)).Append('\n');
            text.Append("describe|").Append(Describe(service)).Append('\n');
        }

        foreach (var service in EvaluationInstances)
        {
            text.Append("evaluation|").Append(EvaluationText(service)).Append('\n');
            text.Append("describe|").Append(Describe(service)).Append('\n');
        }

        foreach (var distractor in Distractors)
        {
            text.Append("distractor|").Append(distractor.Text).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Refuses a task set that could leak its answers or that is not the design the pre-registration describes.</summary>
    /// <exception cref="TaskSetException">The first problem found.</exception>
    public void Validate()
    {
        if (Clusters.Count != RolloutStrategies.All.Count || Clusters.Distinct(StringComparer.Ordinal).Count() != Clusters.Count)
        {
            throw new TaskSetException($"The transfer task set needs {RolloutStrategies.All.Count} distinct clusters, one per strategy.");
        }

        // The hidden trait is a bijection: every cluster maps to a valid strategy, and no two clusters share one.
        if (StrategyByCluster.Count != Clusters.Count
            || Clusters.Any(cluster => !StrategyByCluster.TryGetValue(cluster, out var strategy) || !RolloutStrategies.All.Contains(strategy))
            || StrategyByCluster.Values.Distinct(StringComparer.Ordinal).Count() != Clusters.Count)
        {
            throw new TaskSetException("The cluster-to-strategy mapping is not a bijection onto the strategies apply_migration accepts.");
        }

        if (LearningServices.Count != Clusters.Count
            || LearningServices.Select((service, index) => service.Index == index && service.Cluster == Clusters[index]).Contains(false))
        {
            throw new TaskSetException("There must be exactly one learning service per cluster, in cluster order.");
        }

        // An unknown cluster would only surface later, as a missing key in the middle of a paid run.
        if (EvaluationInstances.Select(service => service.Cluster).Concat(Distractors.Select(distractor => distractor.Cluster))
            .FirstOrDefault(cluster => !Clusters.Contains(cluster, StringComparer.Ordinal)) is { } unknown)
        {
            throw new TaskSetException($"A task names the cluster '{unknown}', which is not one of the task set's clusters.");
        }

        if (EvaluationInstances.Select((service, index) => service.Index == index).Contains(false)
            || Clusters.Any(cluster => EvaluationInstances.Count(service => service.Cluster == cluster) != 2))
        {
            throw new TaskSetException("There must be exactly two evaluation instances per cluster, indexed in order.");
        }

        if (Distractors.Select((distractor, index) => distractor.Index == index).Contains(false)
            || Clusters.Any(cluster => Enum.GetValues<DistractorFamily>().Any(family => Distractors.Count(distractor => distractor.Cluster == cluster && distractor.Family == family) != 2)))
        {
            throw new TaskSetException("There must be exactly two distractors per cluster in each family, indexed in order.");
        }

        if (Distractors.Any(distractor => distractor.Family == DistractorFamily.ConfigRollout && (string.IsNullOrEmpty(distractor.Change) || string.IsNullOrEmpty(distractor.Description))))
        {
            throw new TaskSetException("A config-rollout distractor has no change or description.");
        }

        // Every evaluation service is unseen: no service name repeats across roles.
        var names = LearningServices.Select(service => service.Service)
            .Concat(EvaluationInstances.Select(service => service.Service))
            .Concat(Distractors.Select(distractor => distractor.Service))
            .ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new TaskSetException("Two tasks share a service: every evaluation service must be one no learning run touched.");
        }

        // A cluster is named only as the cluster: no service name contains a cluster's name.
        if (names.FirstOrDefault(name => Clusters.Any(cluster => name.Contains(cluster, StringComparison.OrdinalIgnoreCase))) is { } leaky)
        {
            throw new TaskSetException($"The service '{leaky}' contains a cluster's name.");
        }

        var migrations = LearningServices.Concat(EvaluationInstances).ToList();
        if (migrations.Select(service => service.Migration).Distinct(StringComparer.Ordinal).Count() != migrations.Count
            || migrations.Select(service => service.Description).Distinct(StringComparer.Ordinal).Count() != migrations.Count)
        {
            throw new TaskSetException("Two tasks share a migration or its description.");
        }

        // Nothing the agent reads names a strategy: no task text, no describe_service output.
        var texts = LearningServices.Select(service => ($"learning text of {service.Service}", LearningText(service), service.Service, service.Cluster))
            .Concat(EvaluationInstances.Select(service => ($"evaluation text of {service.Service}", EvaluationText(service), service.Service, service.Cluster)))
            .Concat(Distractors.Select(distractor => ($"distractor text of {distractor.Service}", distractor.Text, distractor.Service, distractor.Cluster)))
            .Concat(migrations.Select(service => ($"describe_service output of {service.Service}", Describe(service), service.Service, service.Cluster)));
        foreach (var (what, text, service, cluster) in texts)
        {
            if (RolloutStrategies.FirstNamedIn(text) is { } named)
            {
                throw new TaskSetException($"The {what} names the strategy '{named}'.");
            }

            // Every text names its service and its cluster.
            if (!text.Contains(service, StringComparison.Ordinal) || !text.Contains(cluster, StringComparison.Ordinal))
            {
                throw new TaskSetException($"The {what} does not name its service and its cluster.");
            }
        }
    }

    private static TransferTaskSet Build()
    {
        string[] clusters = ["halyard", "keel", "mizzen", "bowsprit", "capstan", "taffrail"];

        // The hidden trait. Cluster i accepts strategy (5i + 1) mod 6 of the listing order: a bijection, and not the
        // listing order, so a model that tries strategies in the order the tool lists them is not favoured.
        var strategyByCluster = clusters
            .Select((cluster, index) => (cluster, strategy: RolloutStrategies.All[((5 * index) + 1) % RolloutStrategies.All.Count]))
            .ToDictionary(pair => pair.cluster, pair => pair.strategy, StringComparer.Ordinal);

        TransferService[] learning =
        [
            new(0, "ledgerpost", "halyard", "2026_08_ledgerpost_add_batch_ref", "adds a batch_ref column to postings", new ServiceFacts("PostgreSQL 16", 310, 1400, 2)),
            new(1, "basketsync", "keel", "2026_08_basketsync_add_device_index", "adds an index on baskets(device_id)", new ServiceFacts("PostgreSQL 17", 85, 2600, 1)),
            new(2, "couponvault", "mizzen", "2026_08_couponvault_widen_code", "widens coupons.code from varchar(16) to varchar(40)", new ServiceFacts("PostgreSQL 16", 14, 120, 3)),
            new(3, "stockpile", "bowsprit", "2026_08_stockpile_add_bin_column", "adds a bin_location column to stock_items", new ServiceFacts("PostgreSQL 17", 640, 900, 2)),
            new(4, "parcelroute", "capstan", "2026_08_parcelroute_add_zone_index", "adds an index on routes(zone_id, created_at)", new ServiceFacts("PostgreSQL 16", 1150, 3800, 1)),
            new(5, "pricebook", "taffrail", "2026_08_pricebook_add_currency_check", "adds a check constraint on price_entries.currency", new ServiceFacts("PostgreSQL 17", 42, 260, 3)),
        ];

        // Instance i runs in cluster i mod 6, so the rotated condition order meets every cluster at two offsets.
        TransferService[] evaluation =
        [
            new(0, "refundflow", "halyard", "2026_10_refundflow_reason_code", "introduces a nullable reason_code on refunds", new ServiceFacts("PostgreSQL 17", 26, 340, 1)),
            new(1, "wishlist", "keel", "2026_10_wishlist_owner_lookup", "creates a lookup index for wishlist_items by owner", new ServiceFacts("PostgreSQL 16", 230, 700, 3)),
            new(2, "giftcards", "mizzen", "2026_10_giftcards_balance_precision", "extends gift_cards.balance to numeric(14,4)", new ServiceFacts("PostgreSQL 17", 9, 60, 2)),
            new(3, "reviewhub", "bowsprit", "2026_10_reviewhub_moderation_flag", "introduces a moderation flag on reviews", new ServiceFacts("PostgreSQL 16", 480, 1900, 1)),
            new(4, "taxcalc", "capstan", "2026_10_taxcalc_region_lookup", "creates a lookup index for tax_rates by region", new ServiceFacts("PostgreSQL 17", 3, 40, 2)),
            new(5, "searchindex", "taffrail", "2026_10_searchindex_synonym_table", "introduces a synonyms table for query expansion", new ServiceFacts("PostgreSQL 16", 780, 3100, 3)),
            new(6, "loyaltyclub", "halyard", "2026_10_loyaltyclub_tier_expiry", "introduces a tier_expires_at timestamp on members", new ServiceFacts("PostgreSQL 16", 160, 500, 3)),
            new(7, "authgate", "keel", "2026_10_authgate_token_lookup", "creates a lookup index for sessions by token_hash", new ServiceFacts("PostgreSQL 17", 1400, 4500, 2)),
            new(8, "mailqueue", "mizzen", "2026_10_mailqueue_priority_flag", "introduces a priority column on outbound messages", new ServiceFacts("PostgreSQL 16", 920, 2200, 1)),
            new(9, "fraudwatch", "bowsprit", "2026_10_fraudwatch_score_precision", "extends risk_scores.score to numeric(8,5)", new ServiceFacts("PostgreSQL 17", 55, 800, 3)),
            new(10, "shipquote", "capstan", "2026_10_shipquote_carrier_lookup", "creates a lookup index for quotes by carrier", new ServiceFacts("PostgreSQL 16", 18, 150, 2)),
            new(11, "returnsdesk", "taffrail", "2026_10_returnsdesk_label_uniqueness", "enforces unique label_id values on return_labels", new ServiceFacts("PostgreSQL 17", 120, 90, 1)),
        ];

        TransferDistractor[] distractors =
        [
            new(0, "sessionstore", "halyard"),
            new(1, "imagecdn", "keel"),
            new(2, "pagecache", "mizzen"),
            new(3, "geolookup", "bowsprit"),
            new(4, "ratelimiter", "capstan"),
            new(5, "feedbuilder", "taffrail"),
            new(6, "currencyfx", "halyard"),
            new(7, "bannerads", "keel"),
            new(8, "sitemapgen", "mizzen"),
            new(9, "cmsblocks", "bowsprit"),
            new(10, "abtests", "capstan"),
            new(11, "nightlyexport", "taffrail"),

            // Config rollouts: real tickets in the same clusters, worded like the migration tickets, whose tool takes no
            // strategy. Written once, before they were first run.
            new(12, "checkoutapi", "halyard", DistractorFamily.ConfigRollout, "2026_09_checkoutapi_pool_limit", "raises the database proxy's connection pool limit from 200 to 320"),
            new(13, "cartpricing", "keel", DistractorFamily.ConfigRollout, "2026_09_cartpricing_retry_budget", "lowers the retry budget for writes to the production database"),
            new(14, "promoengine", "mizzen", DistractorFamily.ConfigRollout, "2026_09_promoengine_statement_timeout", "sets a 30 second statement timeout on the promotions database"),
            new(15, "inventoryapi", "bowsprit", DistractorFamily.ConfigRollout, "2026_09_inventoryapi_replica_weight", "shifts read traffic towards the newest read replica"),
            new(16, "dispatchhub", "capstan", DistractorFamily.ConfigRollout, "2026_09_dispatchhub_index_hints", "enables index hints for queries on the routes table"),
            new(17, "paymentsui", "taffrail", DistractorFamily.ConfigRollout, "2026_09_paymentsui_column_masking", "turns on column masking for the card_tokens table"),
            new(18, "accountsapi", "halyard", DistractorFamily.ConfigRollout, "2026_09_accountsapi_job_pool", "adds a separate connection pool for migration jobs"),
            new(19, "notifyhub", "keel", DistractorFamily.ConfigRollout, "2026_09_notifyhub_batch_size", "doubles the batch size for outbound message inserts"),
            new(20, "catalogapi", "mizzen", DistractorFamily.ConfigRollout, "2026_09_catalogapi_cache_ttl", "shortens the product cache ttl to 60 seconds"),
            new(21, "warehouseapi", "bowsprit", DistractorFamily.ConfigRollout, "2026_09_warehouseapi_lock_timeout", "sets a 5 second lock timeout for updates to stock_items"),
            new(22, "quotesvc", "capstan", DistractorFamily.ConfigRollout, "2026_09_quotesvc_replica_lag_alert", "alerts when replica lag on the quotes database exceeds 10 seconds"),
            new(23, "reportingapi", "taffrail", DistractorFamily.ConfigRollout, "2026_09_reportingapi_vacuum_window", "moves the nightly vacuum window of the reporting database"),
        ];

        return new TransferTaskSet(CurrentVersion, clusters, strategyByCluster, learning, evaluation, distractors);
    }
}
