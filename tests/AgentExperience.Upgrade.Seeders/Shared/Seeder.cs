// Story 12.1: one seeder source, compiled once per published preview. Each project under this directory references
// exactly one preview's packages from nuget.org and defines that preview's symbol (PREVIEW1, PREVIEW2); the #if blocks
// below are where the published surfaces differ, and the compiler is what checks them. Nothing here writes SQL: the
// database is created by that preview's own migrator and filled through that preview's own services and stores.
//
// Usage: --connection <cs> --manifest <path> [--application <cs>] [--keys <path>]
//   PREVIEW1 (single role): --connection's role migrates and writes, as a preview.1 host did.
//   PREVIEW2 (two roles):   --connection is the owner, which migrates and grants the application role (the username of
//                           --application) its privileges; every write is made as that role. With --keys, every
//                           component runs in crypto-shredding mode, its wrapped keys kept in that file.
// The manifest records exactly what was stored, as this preview's own API returned it.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.Vectors;
using Npgsql;
#if !PREVIEW1
using AgentExperience.Core.Confidence;
using AgentExperience.Upgrade.Seeders;
#endif

#if !PREVIEW1 && !PREVIEW2
#error Define the preview this seeder is compiled for (PREVIEW1 or PREVIEW2).
#endif

try
{
    await Seeder.RunAsync(args);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Seeding {Seeder.Preview} failed: {exception}");
    return 1;
}

internal static class Seeder
{
#if PREVIEW1
    public const string Preview = "0.1.0-preview.1";
#else
    public const string Preview = "0.1.0-preview.2";
#endif

    public const string EmbeddingModel = "upgrade-test-model";

    /// <summary>The text every recorded search runs: it matches the lesson of each verified record.</summary>
    public const string SearchText = "upgrade dataset verified";

    private const string ArtifactRevision = "rev-upgrade-1";

    /// <summary>Payload and event timestamps: fixed, so the dataset is the same on every run.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly ClosedVerificationRound Round = new(Guid.Parse("0c1a0000-0000-4000-8000-000000000001"), ArtifactRevision);

    private static readonly Scope ScopeA = new("upgrade-tenant", "upgrade-app", "upgrade-project", TeamId: "team-a");
    private static readonly Scope ScopeB = new("upgrade-tenant", "upgrade-app", "upgrade-project", TeamId: "team-b");

    private static readonly AuthorizationContext Auth = new("upgrade-tenant", "upgrade-seeder", ["experience:write"], T0);
    private static readonly GrantAdministration Admin = new("upgrade-admin", T0);

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query", "limit" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    /// <summary>The manifest's serializer settings; the upgrade tests read it with the same ones.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task RunAsync(string[] args)
    {
        var options = ParseOptions(args);
        var connection = options.GetValueOrDefault("connection") ?? throw new ArgumentException("--connection is required.");
        var manifestPath = options.GetValueOrDefault("manifest") ?? throw new ArgumentException("--manifest is required.");

        var manifest = new JsonObject
        {
            ["preview"] = Preview,
            ["packageVersion"] = InformationalVersion(typeof(ExperienceSchemaMigrator)),
            // Every AgentExperience assembly this process loaded, with the version and source commit it carries, so the
            // tests can prove the database was written by the published packages and not by this repository's src/.
            ["assemblies"] = new JsonObject
            {
                [typeof(ExperienceRecord).Assembly.GetName().Name!] = InformationalVersion(typeof(ExperienceRecord)),
                [typeof(ExperienceFinalizationService).Assembly.GetName().Name!] = InformationalVersion(typeof(ExperienceFinalizationService)),
                [typeof(ExperienceSchemaMigrator).Assembly.GetName().Name!] = InformationalVersion(typeof(ExperienceSchemaMigrator)),
                [typeof(ExperienceVectorSchemaMigrator).Assembly.GetName().Name!] = InformationalVersion(typeof(ExperienceVectorSchemaMigrator)),
            },
        };

        await using var migrating = NpgsqlDataSource.Create(connection);
        var applied = new List<string>();
        applied.AddRange((await ExperienceSchemaMigrator.MigrateAsync(migrating, CancellationToken.None)).AppliedScripts);
        applied.AddRange((await ExperienceVectorSchemaMigrator.MigrateAsync(migrating, CancellationToken.None)).AppliedScripts);
        manifest["appliedScripts"] = Node(applied);

#if PREVIEW1
        if (options.ContainsKey("application") || options.ContainsKey("keys"))
        {
            throw new ArgumentException("0.1.0-preview.1 is a single-role deployment with no crypto-shredding: pass --connection and --manifest only.");
        }

        var writing = migrating;
        manifest["encrypted"] = false;
        var dataset = new Dataset(writing);
#else
        var applicationConnection = options.GetValueOrDefault("application")
            ?? throw new ArgumentException("0.1.0-preview.2 is a two-role deployment: pass --application too.");
        var applicationRole = new NpgsqlConnectionStringBuilder(applicationConnection).Username!;
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            migrating,
            new ExperienceApplicationRoleOptions(applicationRole) { AllowErasure = true },
            CancellationToken.None);
        await using var writing = NpgsqlDataSource.Create(applicationConnection);
        manifest["applicationRole"] = applicationRole;

        var keys = options.GetValueOrDefault("keys");
        manifest["encrypted"] = keys is not null;
        var dataset = new Dataset(writing, keys is null ? null : new ExperienceEncryption(FileWrappedKeyRepository.OpenKeyStore(keys)));
#endif

        await dataset.WriteAsync(manifest);

        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(Json));
        Console.WriteLine($"Seeded {Preview} ({manifest["packageVersion"]}): {manifest["records"]!.AsArray().Count} records.");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Usage: --connection <cs> --manifest <path> [--application <cs>] [--keys <path>]");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i][2..], args[i + 1]))
            {
                throw new ArgumentException($"Unexpected or repeated option '{args[i]}'.");
            }
        }

        return options;
    }

    private static string? InformationalVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);

    private static void Expect<T>(T actual, T expected, string what)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}.");
        }
    }

    private static Guid Id(int n) => Guid.Parse($"0c1a{n:D4}-0000-4000-8000-000000000000");

    /// <summary>The dataset. Every ID is fixed; only the timestamps the stores and the database stamp themselves vary.</summary>
    private sealed class Dataset
    {
        private readonly PostgresExperienceRecordStore _store;
        private readonly PostgresExperienceCandidateSource _candidates;
        private readonly PostgresExperienceGrantStore _grants;
        private readonly PostgresExperienceGrantAccessLog _accessLog;
        private readonly PostgresExperienceReuseFeedbackStore _feedback;
        private readonly PostgresExperienceEmbeddingIndex _index;
        private readonly InMemoryExperienceCaptureService _capture;
        private readonly ExperienceLifecycleService _lifecycle;
        private readonly ExperienceFinalizationService _finalization;
#if !PREVIEW1
        private readonly ExperienceLifecycleService _verifying;
#endif
        private int _auditFailures;

        private readonly JsonArray _records = [];
        private readonly JsonArray _tombstones = [];
        private readonly JsonArray _grantItems = [];
        private readonly JsonArray _sharedReads = [];
        private readonly JsonArray _feedbackItems = [];
        private readonly JsonArray _embeddings = [];
        private readonly JsonArray _queries = [];
        private readonly JsonArray _searches = [];
        private readonly JsonArray _getMany = [];
        private readonly JsonArray _supersessionChecks = [];
        private readonly JsonArray _accessLogs = [];

#if PREVIEW1
        public Dataset(NpgsqlDataSource dataSource)
        {
            _accessLog = new PostgresExperienceGrantAccessLog(dataSource);
            var auditing = new ExperienceGrantAuditing(_accessLog, _ => _auditFailures++, ExperienceGrantAuditingMode.Required);
            _store = new PostgresExperienceRecordStore(dataSource, auditing: auditing);
            _candidates = new PostgresExperienceCandidateSource(dataSource);
            _grants = new PostgresExperienceGrantStore(dataSource);
            _feedback = new PostgresExperienceReuseFeedbackStore(dataSource);
            _index = new PostgresExperienceEmbeddingIndex(dataSource);
            _lifecycle = new ExperienceLifecycleService(_store);
#else
        public Dataset(NpgsqlDataSource dataSource, ExperienceEncryption? encryption)
        {
            _accessLog = new PostgresExperienceGrantAccessLog(dataSource);
            var auditing = new ExperienceGrantAuditing(_accessLog, _ => _auditFailures++, ExperienceGrantAuditingMode.Required);
            _store = new PostgresExperienceRecordStore(dataSource, auditing: auditing, encryption: encryption);
            _candidates = new PostgresExperienceCandidateSource(dataSource, encryption: encryption);
            _grants = new PostgresExperienceGrantStore(dataSource, encryption: encryption);
            _feedback = new PostgresExperienceReuseFeedbackStore(dataSource, encryption: encryption);
            _index = new PostgresExperienceEmbeddingIndex(dataSource, encryption: encryption);

            // preview.2 verifies independence by default. Most of this dataset's evidence names runs nothing was ever
            // exposed in, so it takes the documented opt-out and is stored HostTrusted; the exposure-bound evidence
            // goes through _verifying and is stored Verified.
            _lifecycle = new ExperienceLifecycleService(
                _store,
                indexingService: null,
                new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });
            _verifying = new ExperienceLifecycleService(_store, indexingService: null, new ExperienceIndependenceOptions());
#endif
            _capture = new InMemoryExperienceCaptureService(
                new DefaultSanitizer(Sanitization),
                new CaptureLimits(MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10_000, MaxErrorLength: 10_000));
            _finalization = new ExperienceFinalizationService(_capture, new DefaultExperienceReflector(), _store, _lifecycle);
        }

        public async Task WriteAsync(JsonObject manifest)
        {
            // Scope A: Validated (with supporting evidence and an embedding), Contested (after contradicting
            // evidence), Superseded (replaced by the validated one).
            var validated = await FinalizeAsync(ScopeA, run: 1, "a-validated", verified: true);
            await EvidenceAsync(_lifecycle, ScopeA, validated, n: 101, ConfidenceEvidenceKind.Supporting, runId: Id(6101), roundId: Id(7101), "supporting evidence before the upgrade");

            // Contradicting evidence is what contests a record: the lifecycle service moves it to Contested itself.
            var contested = await FinalizeAsync(ScopeA, run: 2, "a-contested", verified: true);
            await EvidenceAsync(_lifecycle, ScopeA, contested, n: 102, ConfidenceEvidenceKind.Contradicting, runId: Id(6102), roundId: Id(7102), "contradicting evidence before the upgrade");

            var superseded = await FinalizeAsync(ScopeA, run: 3, "a-superseded", verified: true);
            await TransitionAsync(ScopeA, superseded, n: 203, ExperienceStatus.Validated, ExperienceStatus.Superseded, replacement: validated);

            var names = new List<(string Name, Scope Scope, Guid Id, ExperienceStatus Status)>
            {
                ("a-validated", ScopeA, validated, ExperienceStatus.Validated),
                ("a-contested", ScopeA, contested, ExperienceStatus.Contested),
                ("a-superseded", ScopeA, superseded, ExperienceStatus.Superseded),
            };

#if !PREVIEW1
            // Exposure-bound evidence (preview.2): run 7 is delivered a-validated, finalized, and its machine evidence
            // about a-validated passes verification. Run 8 is exposed the same way and finalized, but no evidence names
            // it: the upgrade tests submit that evidence with today's verifying lifecycle service.
            var revision = (await _store.GetAsync(Auth, ScopeA, validated, CancellationToken.None)).Record!.Revision;
            var reuser = await FinalizeAsync(ScopeA, run: 7, "a-reuser-1", verified: true, exposedTo: [new RunExposure(validated, revision)]);
            var verifiedEvidence = await EvidenceAsync(_verifying, ScopeA, validated, n: 103, ConfidenceEvidenceKind.Supporting, runId: Id(7), roundId: Round.RoundId, "exposure-bound evidence before the upgrade");
            Expect(verifiedEvidence.Update?.Admission?.ToString() ?? "none", nameof(ConfidenceEvidenceAdmission.Verified), "evidence 103's admission");

            revision = (await _store.GetAsync(Auth, ScopeA, validated, CancellationToken.None)).Record!.Revision;
            var exposedRun = await FinalizeAsync(ScopeA, run: 8, "a-reuser-2", verified: true, exposedTo: [new RunExposure(validated, revision)]);
            names.Add(("a-reuser-1", ScopeA, reuser, ExperienceStatus.Validated));
            names.Add(("a-reuser-2", ScopeA, exposedRun, ExperienceStatus.Validated));
            manifest["exposedRun"] = new JsonObject
            {
                ["runId"] = Id(8),
                ["experienceId"] = exposedRun,
                ["targetId"] = validated,
                ["scope"] = Node(ScopeA),
                ["verificationRoundId"] = Round.RoundId,
            };
#endif

            // Scope B: Quarantined (verification failed), Validated and shared with scope A (with an embedding), and one
            // record erased into a tombstone.
            var quarantined = await FinalizeAsync(ScopeB, run: 4, "b-quarantined", verified: false);
            var shared = await FinalizeAsync(ScopeB, run: 5, "b-shared", verified: true);
            var erased = await FinalizeAsync(ScopeB, run: 6, "b-erased", verified: true);
            names.Add(("b-quarantined", ScopeB, quarantined, ExperienceStatus.Quarantined));
            names.Add(("b-shared", ScopeB, shared, ExperienceStatus.Validated));

            // Grants: a live one on b-shared to scope A, and a revoked one on a-validated to scope B.
            var liveGrant = await GrantAsync(n: 301, shared, ScopeB, ScopeA, "shared with team A before the upgrade", live: true, DateTimeOffset.UtcNow.AddDays(30));
            var revokedGrant = await GrantAsync(n: 302, validated, ScopeA, ScopeB, "shared with team B, then revoked", live: false, DateTimeOffset.UtcNow.AddDays(30));

            // Reuse feedback: one unattributed submission, one human-attributed one.
            await FeedbackAsync(n: 401, "f-unattributed", human: false, [validated, contested]);
            await FeedbackAsync(n: 402, "f-human", human: true, [validated]);

            // Embeddings on the two eligible records, at their final revisions.
            var embeddings = new[]
            {
                ("e-a-validated", ScopeA, validated, new[] { 0.6f, 0.8f, 0f, 0f }),
                ("e-b-shared", ScopeB, shared, new[] { 0f, 0f, 0.8f, 0.6f }),
            };
            foreach (var (name, scope, id, vector) in embeddings)
            {
                await EmbedAsync(name, scope, id, vector);
            }

            // The erasure, last among the writes to scope B's records.
            var deleted = await _store.DeleteAsync(Auth, ScopeB, erased, CancellationToken.None);
            Expect(deleted.Outcome, ExperienceStoreOutcome.Deleted, "erasing b-erased");
            var afterDelete = await _store.GetAsync(Auth, ScopeB, erased, CancellationToken.None);
            var tombstoneHistory = await _store.GetHistoryAsync(Auth, new ExperienceRecordHistoryQuery(ScopeB, erased), CancellationToken.None);
            _tombstones.Add(new JsonObject
            {
                ["name"] = "b-erased",
                ["experienceId"] = erased,
                ["scope"] = Node(ScopeB),
                ["deleteRevision"] = deleted.Revision,
                ["readOutcome"] = afterDelete.Outcome.ToString(),
                ["historyOutcome"] = tombstoneHistory.Outcome.ToString(),
                ["historyRevision"] = tombstoneHistory.Revision,
            });

            // Read everything back through this preview's API, after the last write to the records.
            foreach (var (name, scope, id, status) in names)
            {
                await ReadRecordAsync(name, scope, id, status);
            }

            foreach (var (name, scope, id, vector) in embeddings)
            {
                await ReadEmbeddingAsync(name, scope, id, vector);
            }

            foreach (var scope in new[] { ScopeA, ScopeB })
            {
                var name = scope.TeamId!;
                var query = await _store.QueryAsync(Auth, new ExperienceRecordQuery(scope), CancellationToken.None);
                Expect(query.Outcome, ExperienceStoreOutcome.Found, $"querying {name}");
                _queries.Add(new JsonObject { ["name"] = $"query-{name}", ["scope"] = Node(scope), ["result"] = Node(query) });

                var search = await _candidates.SearchAsync(Auth, new ExperienceCandidateQuery(scope, SearchText, Eligible, MinimumConfidence: 0), CancellationToken.None);
                Expect(search.Outcome, ExperienceStoreOutcome.Found, $"searching {name}");
                _searches.Add(new JsonObject { ["name"] = $"search-{name}", ["scope"] = Node(scope), ["text"] = SearchText, ["result"] = Node(search) });
            }

#if !PREVIEW1
            var ids = new List<Guid> { validated, contested, superseded, Id(9999) };
            var many = await _store.GetManyAsync(Auth, ScopeA, ids, new ExperienceReadOptions(), CancellationToken.None);
            Expect(many.Outcome, ExperienceStoreOutcome.Found, "reading scope A's records as a batch");
            _getMany.Add(new JsonObject { ["name"] = "get-many-team-a", ["scope"] = Node(ScopeA), ["ids"] = Node(ids), ["result"] = Node(many) });
#endif

            foreach (var (name, experienceId, replacementId) in new[]
            {
                ("supersede-a-contested-by-a-validated", contested, validated),
                ("supersede-a-validated-by-a-superseded", validated, superseded),
            })
            {
                var check = await _store.CheckSupersessionAsync(Auth, ScopeA, experienceId, replacementId, CancellationToken.None);
                _supersessionChecks.Add(new JsonObject
                {
                    ["name"] = name,
                    ["scope"] = Node(ScopeA),
                    ["experienceId"] = experienceId,
                    ["replacementId"] = replacementId,
                    ["result"] = Node(check),
                });
            }

            await ReadGrantAsync("g-live", ScopeB, shared, liveGrant);
            await ReadGrantAsync("g-revoked", ScopeA, validated, revokedGrant);

            // The shared read through the live grant, audited: it writes a grant access row.
            await SharedReadAsync("b-shared-read-by-a", ScopeA, shared, liveGrant);

            // A grant that expires seconds after seeding, read once while it is live (also audited), so the upgrade
            // tests can watch it expire and purge it.
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(8);
            var expiringGrant = await GrantAsync(n: 303, quarantined, ScopeB, ScopeA, "expires right after the seeding", live: true, expiresAt);
            await ReadGrantAsync("g-expiring", ScopeB, quarantined, expiringGrant);
            var expiringRead = await _store.GetAsync(Auth, ScopeA, quarantined, CancellationToken.None);
            Expect(expiringRead.Outcome, ExperienceStoreOutcome.Found, "reading b-quarantined through the expiring grant");
            manifest["expiringGrant"] = new JsonObject
            {
                ["grantId"] = expiringGrant,
                ["experienceId"] = quarantined,
                ["recordScope"] = Node(ScopeB),
                ["recipientScope"] = Node(ScopeA),
                ["expiresAt"] = Node(_grantItems.Last()!["grant"]!["expiresAt"]),
                ["readWhileLive"] = Node(expiringRead),
            };

            var accesses = await _accessLog.QueryAsync(Auth, new ExperienceGrantAccessQuery(ScopeB, Limit: ExperienceGrantAccessQuery.MaxLimit), CancellationToken.None);
            Expect(accesses.Outcome, ExperienceStoreOutcome.Found, "reading scope B's grant access log");
            Expect(accesses.Accesses.Count, 2, "grant access rows written by the two audited reads");
            Expect(_auditFailures, 0, "grant access rows that were not recorded");
            _accessLogs.Add(new JsonObject { ["name"] = "access-log-team-b", ["recordScope"] = Node(ScopeB), ["result"] = Node(accesses) });

            manifest["records"] = _records;
            manifest["tombstones"] = _tombstones;
            manifest["grants"] = _grantItems;
            manifest["sharedReads"] = _sharedReads;
            manifest["feedback"] = _feedbackItems;
            manifest["embeddings"] = _embeddings;
            manifest["queries"] = _queries;
            manifest["searches"] = _searches;
            manifest["getMany"] = _getMany;
            manifest["supersessionChecks"] = _supersessionChecks;
            manifest["accessLogs"] = _accessLogs;
        }

        private async Task<Guid> FinalizeAsync(Scope scope, int run, string task, bool verified, IReadOnlyList<object>? exposedTo = null)
        {
            var runId = Id(run);
            var at = T0.AddMinutes(run);
            var started = _capture.StartRun(
                runId,
                taskId: task,
                taskDescription: $"upgrade dataset: {task}",
                scope: scope,
                environment: new EnvironmentFingerprint("worker-01", "net10.0", "linux-x64", Preview, new Dictionary<string, string> { ["region"] = "eu-west" }),
                provenance: new Provenance("upgrade-seeder", Preview, at, $"trace-{run}"),
                startedAt: at);
            Expect(started.Outcome, StartRunOutcome.Started, $"starting run {task}");

#if !PREVIEW1
            if (exposedTo is not null)
            {
                var exposure = _capture.RecordExposure(runId, exposedTo.Cast<RunExposure>().ToList());
                Expect(exposure.Outcome, RecordExposureOutcome.Recorded, $"recording run {task}'s exposure");
            }
#else
            if (exposedTo is not null)
            {
                throw new InvalidOperationException("0.1.0-preview.1 records no exposure.");
            }
#endif

            var appended = await _capture.AppendAttemptAsync(runId, new AppendAttemptRequest(
                AttemptId: Id(1000 + run),
                StartedAt: at,
                Duration: TimeSpan.FromSeconds(2),
                ToolCalls:
                [
                    new RawToolCall(
                        ToolCallId: Id(2000 + run),
                        ToolName: "search",
                        Arguments: new Dictionary<string, object?> { ["query"] = $"refund policy {run}", ["limit"] = 5L },
                        StartedAt: at,
                        Duration: TimeSpan.FromMilliseconds(120),
                        Result: "3 documents",
                        Error: null),
                ],
                Result: verified ? "done" : "gave up",
                Error: null));
            Expect(appended.Outcome, AppendAttemptOutcome.Recorded, $"appending {task}'s attempt");

            var completed = await _capture.CompleteRunAsync(runId, Id(3000 + run), RunExecutionStatus.Completed, at.AddMinutes(1));
            Expect(completed.Outcome, CompleteRunOutcome.Recorded, $"completing run {task}");

            var evidence = new Evidence(
                EvidenceId: Id(4000 + run),
                VerificationRoundId: Round.RoundId,
                ArtifactRevision: ArtifactRevision,
                CheckId: "unit-tests-pass",
                Kind: "TestResult",
                Result: verified ? CheckResult.Pass : CheckResult.Fail,
                Producer: "ci",
                Detail: verified ? "42 of 42 passed" : "40 of 42 passed",
                CapturedAt: at.AddSeconds(30));

            var result = await _finalization.FinalizeAsync(new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Auth,
                ClosedRound: Round,
                RequiredChecks: [new RequiredCheck("unit-tests-pass", "TestResult")],
                Evidence: [evidence],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: at.AddMinutes(2)));
            Expect(result.Outcome, verified ? FinalizationOutcome.Validated : FinalizationOutcome.Quarantined, $"finalizing {task}");
            return result.ExperienceId!.Value;
        }

        private static async Task<ApplyConfidenceEvidenceResult> EvidenceAsync(
            ExperienceLifecycleService lifecycle,
            Scope scope,
            Guid experienceId,
            int n,
            ConfidenceEvidenceKind kind,
            Guid runId,
            Guid roundId,
            string reason)
        {
            var result = await lifecycle.ApplyEvidenceAsync(
                Auth,
                new ApplyConfidenceEvidenceRequest(
                    EventId: Id(n),
                    ExperienceId: experienceId,
                    Scope: scope,
                    EvidenceId: Id(5000 + n),
                    Kind: kind,
                    Source: ConfidenceEvidenceSource.Machine,
                    RunId: runId,
                    VerificationRoundId: roundId,
                    Reason: reason,
                    Producer: "upgrade-seeder",
                    OccurredAt: T0.AddHours(1).AddMinutes(n),
                    Detail: $"evidence {n}"),
                CancellationToken.None);
            Expect(result.Outcome, ConfidenceUpdateOutcome.Applied, $"applying evidence {n}");
            return result;
        }

        private async Task TransitionAsync(Scope scope, Guid experienceId, int n, ExperienceStatus prior, ExperienceStatus current, Guid? replacement)
        {
            var revision = (await _store.GetAsync(Auth, scope, experienceId, CancellationToken.None)).Record!.Revision;
            var result = await _lifecycle.CommitAsync(
                Auth,
                new CommitLifecycleTransitionRequest(
                    EventId: Id(n),
                    ExperienceId: experienceId,
                    Scope: scope,
                    PriorStatus: prior,
                    CurrentStatus: current,
                    Reason: $"{prior} to {current} before the upgrade",
                    Producer: "upgrade-seeder",
                    OccurredAt: T0.AddHours(2).AddMinutes(n),
                    ExpectedRevision: revision,
                    ReplacementExperienceId: replacement),
                CancellationToken.None);
            Expect(result.Outcome, LifecycleTransitionOutcome.Committed, $"committing {prior} to {current} (event {n})");
        }

        private async Task<Guid> GrantAsync(int n, Guid experienceId, Scope recordScope, Scope recipientScope, string reason, bool live, DateTimeOffset expiresAt)
        {
            // Whole seconds: the database stores microseconds, and the manifest records what it returns anyway.
            expiresAt = new DateTimeOffset(expiresAt.Ticks - (expiresAt.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
#if PREVIEW1
            var request = new ExperienceGrantRequest(Id(n), experienceId, recordScope, recipientScope, reason, expiresAt);
#else
            // preview.2's disclosure levels: the live grants show the approach, the revoked one named argument keys.
            var request = live
                ? new ExperienceGrantRequest(Id(n), experienceId, recordScope, recipientScope, reason, expiresAt, ExperienceGrantDisclosure.LessonAndApproach)
                : new ExperienceGrantRequest(
                    Id(n),
                    experienceId,
                    recordScope,
                    recipientScope,
                    reason,
                    expiresAt,
                    ExperienceGrantDisclosure.LessonApproachAndArguments,
                    new Dictionary<string, IReadOnlyList<string>> { ["search"] = ["query"] });
#endif
            var created = await _grants.CreateAsync(Auth, Admin, request, CancellationToken.None);
            Expect(created.Outcome, ExperienceGrantOutcome.Created, $"issuing grant {n}");

            if (!live)
            {
                var revoked = await _grants.RevokeAsync(Auth, Admin, new ExperienceGrantRevocation(Id(n), recordScope, "revoked before the upgrade"), CancellationToken.None);
                Expect(revoked.Outcome, ExperienceGrantOutcome.Revoked, $"revoking grant {n}");
            }

            return Id(n);
        }

        private async Task SharedReadAsync(string name, Scope recipient, Guid experienceId, Guid grantId)
        {
            var read = await _store.GetAsync(Auth, recipient, experienceId, CancellationToken.None);
            Expect(read.Outcome, ExperienceStoreOutcome.Found, $"reading {name} through its grant");
            _sharedReads.Add(new JsonObject
            {
                ["name"] = name,
                ["recipientScope"] = Node(recipient),
                ["experienceId"] = experienceId,
                ["grantId"] = grantId,
                ["result"] = Node(read),
            });
        }

        private async Task FeedbackAsync(int n, string name, bool human, IReadOnlyList<Guid> exposed)
        {
            var ordered = exposed.OrderBy(id => id).ToList();
            var feedback = human
                ? new RecordedExperienceReuseFeedback(
                    FeedbackId: Id(n),
                    RunId: Id(8000 + n),
                    Scope: ScopeA,
                    RunOutcome: TaskVerificationStatus.Verified,
                    ClaimedBenefit: ExperienceReuseBenefit.Improved,
                    Benefit: ExperienceReuseBenefit.Improved,
                    AttributionSource: ReuseAttributionSource.HumanAssessment,
                    ReviewerIdentity: "reviewer-1",
                    EvaluatorId: null,
                    VerificationRoundId: null,
                    AssessmentId: Id(9000 + n),
                    Rationale: "the lesson named the right policy",
                    EvidenceIds: [],
                    AttributedAt: T0.AddHours(3),
                    Measure: new ReuseMeasure("minutes-to-resolve", 4.5),
                    TrialLabel: null,
                    ObservedAt: T0.AddHours(3),
                    Exposures: ordered.Select((id, i) => new ExperienceReuseExposure(id, Attributed: true, EvidenceId: Id(9500 + n + i))).ToList())
                : new RecordedExperienceReuseFeedback(
                    FeedbackId: Id(n),
                    RunId: Id(8000 + n),
                    Scope: ScopeA,
                    RunOutcome: TaskVerificationStatus.Verified,
                    ClaimedBenefit: ExperienceReuseBenefit.Improved,
                    Benefit: ExperienceReuseBenefit.Unknown,
                    AttributionSource: ReuseAttributionSource.None,
                    ReviewerIdentity: null,
                    EvaluatorId: null,
                    VerificationRoundId: null,
                    AssessmentId: null,
                    Rationale: null,
                    EvidenceIds: [],
                    AttributedAt: null,
                    Measure: new ReuseMeasure("duration-seconds", 12.5),
                    TrialLabel: "trial-1",
                    ObservedAt: T0.AddHours(3),
                    Exposures: ordered.Select(id => new ExperienceReuseExposure(id, Attributed: false, EvidenceId: null)).ToList());

            var recorded = await _feedback.RecordAsync(Auth, feedback, CancellationToken.None);
            Expect(recorded.Outcome, ExperienceReuseFeedbackStoreOutcome.Recorded, $"recording feedback {name}");

            // What the ledger holds: this preview has no read port for it, so the submission is replayed, and the store
            // hands back the stored row only when every field and exposure is identical.
            var replayed = await _feedback.RecordAsync(Auth, feedback, CancellationToken.None);
            Expect(replayed.Outcome, ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, $"replaying feedback {name}");
            _feedbackItems.Add(new JsonObject
            {
                ["name"] = name,
                ["feedbackId"] = Id(n),
                ["scope"] = Node(ScopeA),
                ["feedback"] = Node(replayed.Feedback),
            });
        }

        private async Task EmbedAsync(string name, Scope scope, Guid experienceId, float[] vector)
        {
            var record = (await _store.GetAsync(Auth, scope, experienceId, CancellationToken.None)).Record!;
            var summary = ExperienceRetrievalSummary.For(record);
            var descriptor = new ExperienceEmbeddingDescriptor(
                EmbeddingModel,
                vector.Length,
                ExperienceEmbeddingDescriptor.ComputeContentHash(EmbeddingModel, summary),
                record.Revision);
            var written = await _index.WriteAsync(Auth, new ExperienceIndexWrite(scope, experienceId, descriptor, vector), CancellationToken.None);
            Expect(written.Outcome, ExperienceIndexOutcome.Written, $"writing embedding {name}");
        }

        private async Task ReadEmbeddingAsync(string name, Scope scope, Guid experienceId, float[] vector)
        {
            var scanned = await _index.ScanAsync(
                Auth,
                new ExperienceIndexScan(scope, EmbeddingModel, Eligible, MinimumConfidence: 0, ExperienceIds: [experienceId]),
                CancellationToken.None);
            Expect(scanned.Outcome, ExperienceStoreOutcome.Found, $"scanning embedding {name}");

            // The scan reports the stored descriptor, not the vector: this preview's API never returns a stored vector.
            // The search does report how close each stored vector is to the query, so the relevances are recorded too.
            var searched = await _index.SearchAsync(
                Auth,
                new ExperienceVectorQuery(scope, EmbeddingModel, vector, Eligible, MinimumConfidence: 0, Limit: 10),
                CancellationToken.None);
            Expect(searched.Outcome, ExperienceVectorSearchOutcome.Found, $"searching embedding {name}");

            _embeddings.Add(new JsonObject
            {
                ["name"] = name,
                ["experienceId"] = experienceId,
                ["scope"] = Node(scope),
                ["modelId"] = EmbeddingModel,
                ["vector"] = Node(vector),
                ["target"] = Node(scanned.Targets.Single()),
                ["searchHits"] = Node(searched.Candidates.Select(candidate => new { candidate.Record.ExperienceId, candidate.Relevance, candidate.SharedByGrant, candidate.PermittingGrantId }).ToList()),
            });
        }

        private async Task ReadRecordAsync(string name, Scope scope, Guid experienceId, ExperienceStatus status)
        {
            var read = await _store.GetAsync(Auth, scope, experienceId, CancellationToken.None);
            Expect(read.Outcome, ExperienceStoreOutcome.Found, $"reading {name}");
            Expect(read.Record!.Status, status, $"{name}'s status");

            var history = await _store.GetHistoryAsync(
                Auth,
                new ExperienceRecordHistoryQuery(scope, experienceId, Limit: ExperienceRecordHistoryQuery.MaxLimit),
                CancellationToken.None);
            Expect(history.Outcome, ExperienceStoreOutcome.Found, $"reading {name}'s history");
            if (history.Events.Count == ExperienceRecordHistoryQuery.MaxLimit)
            {
                throw new InvalidOperationException($"{name}'s history does not fit one page.");
            }

            var record = read.Record!;
            _records.Add(new JsonObject
            {
                ["name"] = name,
                ["experienceId"] = experienceId,
                ["scope"] = Node(scope),
                ["status"] = record.Status.ToString(),
                ["revision"] = record.Revision,
                ["eventIds"] = Node(history.Events.Select(stored => stored.Event.EventId).ToList()),
                ["evidenceIds"] = Node(history.Events.Where(stored => stored.Event.Confidence is not null).Select(stored => stored.Event.Confidence!.EvidenceId).ToList()),
                ["result"] = Node(read),
                ["history"] = Node(history),
            });
        }

        private async Task ReadGrantAsync(string name, Scope recordScope, Guid experienceId, Guid grantId)
        {
            var list = await _grants.ListAsync(Auth, recordScope, experienceId, CancellationToken.None);
            Expect(list.Outcome, ExperienceGrantOutcome.Found, $"listing {name}");
            var history = await _grants.GetHistoryAsync(Auth, recordScope, grantId, CancellationToken.None);
            Expect(history.Outcome, ExperienceGrantOutcome.Found, $"reading {name}'s history");

            _grantItems.Add(new JsonObject
            {
                ["name"] = name,
                ["grantId"] = grantId,
                ["experienceId"] = experienceId,
                ["recordScope"] = Node(recordScope),
                ["eventIds"] = Node(history.Events.Select(e => e.EventId).ToList()),
                ["grant"] = Node(list.Grants.Single(grant => grant.GrantId == grantId)),
                ["history"] = Node(history),
            });
        }
    }
}
