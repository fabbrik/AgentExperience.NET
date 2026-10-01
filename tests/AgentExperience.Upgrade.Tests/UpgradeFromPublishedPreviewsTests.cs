using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.Vectors;
using AgentExperience.Tests.Shared;
using AgentExperience.Upgrade.Seeders;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// Story 12.1: a database a <em>published</em> preview created and filled, through that preview's own migrator and
/// stores, upgrades to today's schema with its data intact. For each preview (and, for one with crypto-shredding, once
/// more with it on), on the PostgreSQL major <see cref="PostgresTestImage"/> selects: seed a fresh database with the
/// preview's seeder, run today's migrators as the owner (for <c>0.1.0-preview.1</c>, after moving the single-role
/// database to two roles exactly as the CHANGELOG's <c>0.1.0-preview.2</c> runbook says), then, as the application
/// role, read back every item the seeder's manifest lists, check the documented upgrade behaviour where data changes by
/// design, carry the lifecycle on, and compare the upgraded schema with a fresh install of today's.
/// </summary>
public sealed class UpgradeFromPublishedPreviewsTests
{
    private const string Database = "aen_upgrade";
    private const string FreshDatabase = "aen_fresh";
    private const string RolePassword = "aen-upgrade-password";

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var preview in PublishedPreview.All)
        {
            data.Add(preview.Version, false);
            if (preview.SupportsEncryption)
            {
                data.Add(preview.Version, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_database_the_published_preview_created_upgrades_with_its_data_intact(string version, bool encrypted)
    {
        var preview = PublishedPreview.Named(version);
        var major = PostgresTestImage.Major;
        var report = new UpgradeReport(encrypted ? $"{version} (crypto-shredding)" : version, major);

        await using var container = new PostgreSqlBuilder(PostgresTestImage.Pgvector).Build();
        await container.StartAsync();
        await using var superuser = NpgsqlDataSource.Create(container.GetConnectionString());
        Assert.Equal(major, await ScalarAsync<int>(superuser, "SELECT current_setting('server_version_num')::int / 10000"));

        // ---- Before: the preview's own seeder creates and fills the database.
        var manifestPath = Path.Combine(Path.GetTempPath(), $"aen-upgrade-{Guid.NewGuid():N}.json");
        var keysPath = encrypted ? Path.Combine(Path.GetTempPath(), $"aen-upgrade-keys-{Guid.NewGuid():N}.json") : null;
        string ownerRole;
        string applicationRole;
        try
        {
            if (preview.TwoRoles)
            {
                ownerRole = "aen_owner";
                applicationRole = "aen_app";
                await ExecuteAsync(superuser, $"CREATE ROLE {ownerRole} LOGIN PASSWORD '{RolePassword}'");
                await ExecuteAsync(superuser, $"CREATE ROLE {applicationRole} LOGIN PASSWORD '{RolePassword}'");
                await CreateDatabaseAsync(container, superuser, Database, ownerRole);
                await ExecuteAsync(superuser, OwnerParameterGrant(ownerRole));
                string[] arguments =
                [
                    "--connection", ConnectionString(container, Database, ownerRole),
                    "--manifest", manifestPath,
                    "--application", ConnectionString(container, Database, applicationRole),
                    .. keysPath is null ? Array.Empty<string>() : ["--keys", keysPath],
                ];
                await Seeders.RunAsync(preview, arguments);
            }
            else
            {
                Assert.False(encrypted, $"{version} has no crypto-shredding.");

                // A single-role deployment: the role the application used ran the migrator and owns everything.
                applicationRole = "aen_legacy";
                await ExecuteAsync(superuser, $"CREATE ROLE {applicationRole} LOGIN PASSWORD '{RolePassword}'");
                await CreateDatabaseAsync(container, superuser, Database, applicationRole);
                await ExecuteAsync(superuser, OwnerParameterGrant(applicationRole));
                await Seeders.RunAsync(preview, "--connection", ConnectionString(container, Database, applicationRole), "--manifest", manifestPath);

                // The runbook's step 2, as a superuser: the documented SQL, verbatim, creates the owner role and moves
                // ownership to it. The old role stays the application role, with the same credentials.
                var runbook = DocumentedOwnershipTransfer(out ownerRole);
                await using var inDatabase = NpgsqlDataSource.Create(ConnectionString(container, Database, username: null));
                await ExecuteAsync(inDatabase, runbook);
                await ExecuteAsync(inDatabase, $"ALTER ROLE {ownerRole} PASSWORD '{RolePassword}'");
            }

            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            Assert.Equal(preview.Version, (string?)manifest["preview"]);
            Assert.Equal(encrypted, (bool?)manifest["encrypted"]);

            // The proof the seeder ran the published packages: every AgentExperience assembly it loaded carries the
            // preview's version and the commit its tag names.
            var assemblies = manifest["assemblies"]!.AsObject().ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
            string[] expectedAssemblies = ["AgentExperience.Abstractions", "AgentExperience.Core", "AgentExperience.Storage.Postgres", "AgentExperience.Storage.Postgres.Vectors"];
            report.Check(
                assemblies.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedAssemblies)
                    && assemblies.Values.All(v => v == $"{preview.Version}+{preview.SourceCommit}"),
                "the seeder's packages",
                $"expected the four AgentExperience assemblies at {preview.Version}+{preview.SourceCommit}; the seeder loaded {string.Join(", ", assemblies.Select(p => $"{p.Key} {p.Value}"))}");

            // What the preview's migrators applied, and so what today's must apply on top: the scripts it did not.
            var seeded = manifest["appliedScripts"].Deserialize<List<string>>(UpgradeReport.Json)!;
            Assert.Equal(preview.LastScript, seeded.Where(PostgresExperienceRecordSchema.ScriptNames.Contains).Last());
            var pending = PostgresExperienceRecordSchema.ScriptNames.Except(seeded)
                .Concat(ExperienceVectorSchema.ScriptNames.Except(seeded))
                .ToList();

            // Story 14.4: a plaintext payload with authorship = Model, which no published preview could write (none had a
            // model-backed reflector that declared it), so the test writes it: a copy of a-validated with that one member
            // set, and an untouched copy beside it, in a project of their own so no item the manifest lists sees them.
            // 0021's backfill must flag the first, and only the first. A sealed payload cannot be edited in place, so
            // the encrypted case has none; its check is that every sealed row stays unknown.
            if (!encrypted)
            {
                await using var inDatabase = NpgsqlDataSource.Create(ConnectionString(container, Database, username: null));
                var source = Guid.Parse((string)manifest["records"]!.AsArray().Single(r => (string?)r!["name"] == "a-validated")!["experienceId"]!);
                foreach (var (id, payload) in new[]
                {
                    (Verification.AuthorshipModelId, "jsonb_set(payload, '{reflection,authorship}', '\"Model\"')"),
                    (Verification.AuthorshipTwinId, "payload"),
                })
                {
                    await ExecuteAsync(
                        inDatabase,
                        "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
                        "team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
                        "created_at, updated_at, payload_version, payload) " +
                        $"SELECT '{id}', source_run_id, tenant_id, application_id, '{Verification.AuthorshipProject}', team_id, agent_id, " +
                        "user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
                        $"payload_version, {payload} FROM agent_experience.experience_records WHERE experience_id = '{source}'");
                }
            }

            // ---- The upgrade: today's migrators as the owner, then the application role's privileges (runbook steps 3-4).
            await using var owner = NpgsqlDataSource.Create(ConnectionString(container, Database, ownerRole));
            var applied = await MigrateAsync(owner, applicationRole);
            report.Check(
                applied.SequenceEqual(pending),
                "the migration",
                $"today's migrators applied [{string.Join(", ", applied)}], expected the scripts the preview had not [{string.Join(", ", pending)}]");

            // Story 15.1: the privileges call switched row-level security on every covered table exactly when this run
            // asked it to -- nine tables, the vectors package's included -- so a green run in that mode really read and
            // wrote everything below behind the policies.
            var secured = await ScalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_class WHERE relnamespace = 'agent_experience'::regnamespace AND relrowsecurity AND NOT relforcerowsecurity");
            report.Check(
                secured == (RowLevelSecurityMode.IsOn ? 9 : 0),
                "row-level security",
                $"{secured} tables have row-level security enabled, expected {(RowLevelSecurityMode.IsOn ? 9 : 0)}");

            // ---- After: everything, as the application role.
            await using var application = NpgsqlDataSource.Create(ConnectionString(container, Database, applicationRole));
            var context = new UpgradeContext(preview, encrypted, report, application, owner, manifest, keysPath);
            await new Verification(context).RunAsync(async () =>
            {
                // Schema equivalence: a fresh install of today's schema, with the same roles and the same privileges
                // call, must describe exactly the same catalog.
                await CreateDatabaseAsync(container, superuser, FreshDatabase, ownerRole);
                await using var freshOwner = NpgsqlDataSource.Create(ConnectionString(container, FreshDatabase, ownerRole));
                await MigrateAsync(freshOwner, applicationRole);
                await using var upgradedCatalog = NpgsqlDataSource.Create(ConnectionString(container, Database, username: null));
                await using var freshCatalog = NpgsqlDataSource.Create(ConnectionString(container, FreshDatabase, username: null));
                var difference = SchemaSnapshot.FirstDifference(await SchemaSnapshot.TakeAsync(upgradedCatalog), await SchemaSnapshot.TakeAsync(freshCatalog));
                report.Check(difference is null, "the schema", $"it differs from a fresh install of today's: {difference}");
            });
            report.AssertNothingFailed();
        }
        finally
        {
            File.Delete(manifestPath);
            if (keysPath is not null)
            {
                File.Delete(keysPath);
            }
        }
    }

    /// <summary>Today's migrators and the privileges call, exactly as a deploy runs them; returns what the migrators applied.</summary>
    private static async Task<List<string>> MigrateAsync(NpgsqlDataSource owner, string applicationRole)
    {
        var applied = (await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None)).AppliedScripts
            .Concat((await ExperienceVectorSchemaMigrator.MigrateAsync(owner, CancellationToken.None)).AppliedScripts)
            .ToList();
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(applicationRole) { AllowErasure = true, EnableRowLevelSecurity = RowLevelSecurityMode.IsOn },
            CancellationToken.None);
        return applied;
    }

    /// <summary>
    /// The SQL block under "Upgrading an existing single-role database" in docs/guide/deployment.md, which the
    /// CHANGELOG's 0.1.0-preview.2 runbook (step 2) points at, read from the document so the test runs what a reader
    /// runs. It names its own owner role and placeholder password; the caller replaces the password afterwards.
    /// </summary>
    private static string DocumentedOwnershipTransfer(out string ownerRole)
    {
        var guide = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "docs", "guide", "deployment.md")).ReplaceLineEndings("\n");
        var section = guide.IndexOf("\n### Upgrading an existing single-role database\n", StringComparison.Ordinal);
        Assert.True(section >= 0, "docs/guide/deployment.md has no 'Upgrading an existing single-role database' section.");
        var block = Regex.Match(guide[section..], "```sql\n(?<sql>.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(block.Success, "That section has no SQL block.");

        var sql = block.Groups["sql"].Value;
        var role = Regex.Match(sql, @"CREATE ROLE (?<role>\w+) LOGIN PASSWORD", RegexOptions.CultureInvariant);
        Assert.True(role.Success, "The documented SQL creates no owner role.");
        ownerRole = role.Groups["role"].Value;
        return sql;
    }

    /// <summary>
    /// The one statement a superuser runs before a non-superuser can migrate (docs/guide/deployment.md, "Creating the
    /// roles"): <c>0010</c> and <c>0012</c> name the purge markers in a function's <c>SET</c> clause.
    /// </summary>
    private static string OwnerParameterGrant(string role) =>
        $"GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized TO {role}";

    /// <summary>
    /// A database owned by <paramref name="owner"/>, with pgvector created in it by the superuser: it is not a trusted
    /// extension, so a superuser creates it, as the guide says.
    /// </summary>
    private static async Task CreateDatabaseAsync(PostgreSqlContainer container, NpgsqlDataSource superuser, string database, string owner)
    {
        await ExecuteAsync(superuser, $"CREATE DATABASE {database} OWNER {owner}");
        await using var inDatabase = NpgsqlDataSource.Create(ConnectionString(container, database, username: null));
        await ExecuteAsync(inDatabase, "CREATE EXTENSION IF NOT EXISTS vector");
    }

    /// <summary><paramref name="database"/>, as <paramref name="username"/>, or as the container's superuser when it is null.</summary>
    private static string ConnectionString(PostgreSqlContainer container, string database, string? username)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database };
        if (username is not null)
        {
            builder.Username = username;
            builder.Password = RolePassword;
        }

        return builder.ConnectionString;
    }

    internal static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>What one upgrade case verifies against.</summary>
internal sealed record UpgradeContext(
    PublishedPreview Preview,
    bool Encrypted,
    UpgradeReport Report,
    NpgsqlDataSource Application,
    NpgsqlDataSource Owner,
    JsonObject Manifest,
    string? KeysPath);

/// <summary>
/// The checks, in order: every read first, then the writes that carry the lifecycle on as the application role, then
/// the catalog, and last the reuse feedback replay, the one read that would write if the ledger had lost a row.
/// </summary>
internal sealed class Verification
{
    /// <summary>The project the authorship copies live in (story 14.4), outside every scope the manifest lists.</summary>
    internal const string AuthorshipProject = "upgrade-authorship";

    /// <summary>The copy of a-validated whose plaintext payload says a model wrote its reflection.</summary>
    internal static readonly Guid AuthorshipModelId = Guid.Parse("14040000-0000-0000-0000-000000000001");

    /// <summary>The untouched copy of a-validated beside it.</summary>
    internal static readonly Guid AuthorshipTwinId = Guid.Parse("14040000-0000-0000-0000-000000000002");

    private const string EmbeddingModel = "upgrade-test-model";

    private static readonly AuthorizationContext Auth = new("upgrade-tenant", "upgrade-tests", ["experience:write"], DateTimeOffset.UtcNow);
    private static readonly GrantAdministration Admin = new("upgrade-tests-admin", DateTimeOffset.UtcNow);
    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly UpgradeContext _context;
    private readonly UpgradeReport _report;
    private readonly ExperienceEncryption? _encryption;
    private readonly PostgresExperienceRecordStore _store;
    private readonly PostgresExperienceCandidateSource _candidates;
    private readonly PostgresExperienceGrantStore _grants;
    private readonly PostgresExperienceGrantAccessLog _accessLog;
    private readonly PostgresExperienceReuseFeedbackStore _feedback;
    private readonly PostgresExperienceEmbeddingIndex _index;

    public Verification(UpgradeContext context)
    {
        _context = context;
        _report = context.Report;

        // Today's components, with today's crypto-shredding over the keys the preview wrapped when the case is encrypted.
        _encryption = context.KeysPath is null ? null : new ExperienceEncryption(FileWrappedKeyRepository.OpenKeyStore(context.KeysPath));
        _store = new PostgresExperienceRecordStore(context.Application, encryption: _encryption);
        _candidates = new PostgresExperienceCandidateSource(context.Application, encryption: _encryption);
        _grants = new PostgresExperienceGrantStore(context.Application, encryption: _encryption);
        _accessLog = new PostgresExperienceGrantAccessLog(context.Application);
        _feedback = new PostgresExperienceReuseFeedbackStore(context.Application, encryption: _encryption);
        _index = new PostgresExperienceEmbeddingIndex(context.Application, encryption: _encryption);
    }

    private bool Preview1 => !_context.Preview.TwoRoles;

    public async Task RunAsync(Func<Task> schemaEquivalence)
    {
        (string Name, Func<Task> Step)[] steps =
        [
            // Reads.
            ("the records", RecordsAsync),
            ("the batch reads", BatchReadsAsync),
            ("the queries and text searches", QueriesAndSearchesAsync),
            ("the supersession checks", SupersessionChecksAsync),
            ("the tombstones", TombstonesAsync),
            ("the grants", GrantsAsync),
            ("the shared reads", SharedReadsAsync),
            ("the grant access log", AccessLogAsync),
            ("the embeddings", EmbeddingsAsync),
            ("the storage mode", StorageModeAsync),
            ("the reflection authorship", AuthorshipAsync),

            // Writes, as the application role.
            ("a new transition", TransitionAsync),
            ("new confidence evidence", EvidenceAsync),
            ("the expiring grant", ExpiryAsync),
            ("a new grant", NewGrantAsync),
            ("an erasure", ErasureAsync),
            ("new reuse feedback", NewFeedbackAsync),
            ("a new embedding", NewEmbeddingAsync),

            // The catalog.
            ("the migration journal", JournalAsync),
            ("the schema", schemaEquivalence),

            // Last: a replay writes if the ledger lost the row.
            ("the reuse feedback", FeedbackReplayAsync),
        ];

        // A step that throws is a failure of that step, reported with the rest, not the end of the run.
        foreach (var (name, step) in steps)
        {
            try
            {
                await step();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _report.Check(false, name, $"checking it threw {exception.GetType().Name}: {UpgradeReport.Truncate(exception.Message + (exception.InnerException is { } inner ? " / " + inner.Message : string.Empty))}");
            }
        }
    }

    // ------------------------------------------------------------------ reads

    private async Task RecordsAsync()
    {
        foreach (var item in Items("records"))
        {
            var (name, scope, id) = Identify(item);
            var read = await _store.GetAsync(Auth, scope, id, CancellationToken.None);
            _report.Compare($"record {name} ({id})", item["result"], read);

            var history = await _store.GetHistoryAsync(
                Auth,
                new ExperienceRecordHistoryQuery(scope, id, Limit: ExperienceRecordHistoryQuery.MaxLimit),
                CancellationToken.None);
            _report.Compare($"lifecycle history of {name} ({id})", item["history"], history);
            _report.Check(
                history.Events.Select(e => e.Event.EventId).SequenceEqual(Guids(item["eventIds"])),
                $"lifecycle events of {name} ({id})",
                $"event IDs [{string.Join(", ", history.Events.Select(e => e.Event.EventId))}], expected [{string.Join(", ", Guids(item["eventIds"]))}]");
            _report.Check(
                history.Events.Where(e => e.Event.Confidence is not null).Select(e => e.Event.Confidence!.EvidenceId).SequenceEqual(Guids(item["evidenceIds"])),
                $"confidence evidence of {name} ({id})",
                "the evidence IDs on its history differ from the manifest's");

            if (Preview1 && read.Record is { } record)
            {
                // Documented (CHANGELOG 0.1.0-preview.2, Breaking changes): a record stored before origins and
                // closed rounds existed reads back HostWritten, with no closed round and exposed to nothing; and
                // evidence admitted before 0018 has no recorded admission.
                _report.Check(
                    record.Origin == ExperienceRecordOrigin.HostWritten && record.ClosedRoundId is null && record.Provenance.ExposedTo.Count == 0,
                    $"record {name} ({id})",
                    $"a preview.1 record must read back HostWritten, with no closed round and no exposures; got {record.Origin}, {record.ClosedRoundId}, {record.Provenance.ExposedTo.Count} exposures");
                _report.Check(
                    history.Events.All(e => e.Event.Confidence is null || e.Event.Confidence.Admission is null),
                    $"confidence evidence of {name} ({id})",
                    "evidence admitted before 0018 must read back with no recorded admission");
            }
        }

        if (!Preview1)
        {
            // The exposure-bound data preview.2 wrote survives as such.
            var reuser = Record("a-reuser-2")["result"].Deserialize<ExperienceRecordGetResult>(UpgradeReport.Json)!.Record!;
            _report.Check(reuser.Provenance.ExposedTo.Count == 1 && reuser.Origin == ExperienceRecordOrigin.Finalized, "record a-reuser-2", "the seeder's record lost its exposure or its origin");
            var verified = Record("a-validated")["history"].Deserialize<ExperienceRecordHistoryResult>(UpgradeReport.Json)!;
            _report.Check(
                verified.Events.Any(e => e.Event.Confidence?.Admission == ConfidenceEvidenceAdmission.Verified),
                "confidence evidence of a-validated",
                "the seeder's exposure-bound evidence is not recorded Verified");
        }
    }

    private async Task BatchReadsAsync()
    {
        // Every record, read as a batch in its scope, must be exactly what it read one at a time before the upgrade.
        foreach (var group in Items("records").GroupBy(item => item["scope"]!.ToJsonString()))
        {
            var scope = group.First()["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var ids = group.Select(item => Identify(item).Id).ToList();
            var many = await _store.GetManyAsync(Auth, scope, ids, new ExperienceReadOptions(), CancellationToken.None);
            _report.Check(many.Outcome == ExperienceStoreOutcome.Found && many.Results.Count == ids.Count, $"batch read of {scope.TeamId}", $"it was {many.Outcome} with {many.Results.Count} results for {ids.Count} IDs");
            foreach (var (item, result) in group.Zip(many.Results))
            {
                _report.Compare($"batch read of {Identify(item).Name} ({Identify(item).Id})", item["result"], result);
            }
        }

        // preview.2 had a batch read of its own: its answer, unknown ID included, must be today's.
        foreach (var item in OptionalItems("getMany"))
        {
            var scope = item["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var many = await _store.GetManyAsync(Auth, scope, Guids(item["ids"]), new ExperienceReadOptions(), CancellationToken.None);
            _report.Compare($"batch read {item["name"]}", item["result"], many);
        }
    }

    private async Task QueriesAndSearchesAsync()
    {
        foreach (var item in Items("queries"))
        {
            var scope = item["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var query = await _store.QueryAsync(Auth, new ExperienceRecordQuery(scope), CancellationToken.None);
            _report.Compare($"query {item["name"]}", item["result"], query);
        }

        // Text search: over the plaintext search vector, or in crypto-shredding mode over the sealed one.
        foreach (var item in Items("searches"))
        {
            var scope = item["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var search = await _candidates.SearchAsync(
                Auth,
                new ExperienceCandidateQuery(scope, (string)item["text"]!, Eligible, MinimumConfidence: 0),
                CancellationToken.None);
            _report.Compare($"text search {item["name"]}", item["result"], search);
            _report.Check(search.Candidates.Count > 0, $"text search {item["name"]}", "it found nothing, so it proves nothing");
        }
    }

    private async Task SupersessionChecksAsync()
    {
        foreach (var item in Items("supersessionChecks"))
        {
            var scope = item["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var check = await _store.CheckSupersessionAsync(
                Auth,
                scope,
                Guid.Parse((string)item["experienceId"]!),
                Guid.Parse((string)item["replacementId"]!),
                CancellationToken.None);
            _report.Compare($"supersession check {item["name"]}", item["result"], check);
        }
    }

    private async Task TombstonesAsync()
    {
        foreach (var item in Items("tombstones"))
        {
            var (name, scope, id) = Identify(item);
            var read = await _store.GetAsync(Auth, scope, id, CancellationToken.None);
            _report.Check(
                read.Outcome.ToString() == (string?)item["readOutcome"] && read.Outcome == ExperienceStoreOutcome.Deleted,
                $"tombstone {name} ({id})",
                $"reads back {read.Outcome}, expected {item["readOutcome"]}");

            var history = await _store.GetHistoryAsync(Auth, new ExperienceRecordHistoryQuery(scope, id), CancellationToken.None);
            _report.Check(
                history.Outcome.ToString() == (string?)item["historyOutcome"] && history.Revision == (long)item["historyRevision"]!,
                $"tombstone {name} ({id})",
                $"its history reads back {history.Outcome} at revision {history.Revision}, expected {item["historyOutcome"]} at {item["historyRevision"]}");

            await RefusesRecreationAsync($"tombstone {name} ({id})", scope, id);

            if (_context.Encrypted)
            {
                await KeyDestroyedAsync($"tombstone {name} ({id})", scope, id);
            }
        }
    }

    private async Task GrantsAsync()
    {
        foreach (var item in Items("grants"))
        {
            var name = (string)item["name"]!;
            var grantId = Guid.Parse((string)item["grantId"]!);
            var recordScope = item["recordScope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var experienceId = Guid.Parse((string)item["experienceId"]!);

            var list = await _grants.ListAsync(Auth, recordScope, experienceId, CancellationToken.None);
            var grant = list.Grants.SingleOrDefault(g => g.GrantId == grantId);
            _report.Check(grant is not null, $"grant {name} ({grantId})", $"not listed for its record (list outcome {list.Outcome}, {list.Grants.Count} grants)");
            if (grant is not null)
            {
                _report.Compare($"grant {name} ({grantId})", item["grant"], grant);
            }

            var history = await _grants.GetHistoryAsync(Auth, recordScope, grantId, CancellationToken.None);
            _report.Compare($"grant history of {name} ({grantId})", item["history"], history);
            _report.Check(
                history.Events.Select(e => e.EventId).SequenceEqual(Guids(item["eventIds"])),
                $"grant events of {name} ({grantId})",
                "the event IDs differ from the manifest's");

            if (Preview1)
            {
                // Documented (0011, CHANGELOG 0.1.0-preview.2 Behaviour changes): every existing grant becomes
                // LessonOnly, and events written before 0011 carry no recorded level.
                _report.Check(grant?.Disclosure == ExperienceGrantDisclosure.LessonOnly, $"grant {name} ({grantId})", $"0011 must make it LessonOnly; it is {grant?.Disclosure}");
                _report.Check(history.Events.All(e => e.Disclosure is null), $"grant events of {name} ({grantId})", "events written before 0011 must read back with no recorded disclosure");
            }
        }
    }

    private async Task SharedReadsAsync()
    {
        foreach (var item in Items("sharedReads"))
        {
            var name = (string)item["name"]!;
            var recipient = item["recipientScope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var id = Guid.Parse((string)item["experienceId"]!);
            var expected = item["result"]!.DeepClone().AsObject();
            if (!expected.ContainsKey("grantDisclosure"))
            {
                // Documented (0011): a record borrowed through a grant issued before 0011 is read at LessonOnly.
                expected["grantDisclosure"] = nameof(ExperienceGrantDisclosure.LessonOnly);
            }

            var read = await _store.GetAsync(Auth, recipient, id, CancellationToken.None);
            _report.Compare($"shared read {name} ({id} through grant {item["grantId"]})", expected, read);
        }
    }

    private async Task AccessLogAsync()
    {
        // The rows the seeder's audited shared reads wrote. Nothing this suite does is audited, so the log is unchanged.
        foreach (var item in Items("accessLogs"))
        {
            var scope = item["recordScope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var log = await _accessLog.QueryAsync(Auth, new ExperienceGrantAccessQuery(scope, Limit: ExperienceGrantAccessQuery.MaxLimit), CancellationToken.None);
            _report.Compare($"grant access log {item["name"]}", item["result"], log);
            if (Preview1)
            {
                _report.Check(log.Accesses.All(a => a.Disclosure is null), $"grant access log {item["name"]}", "rows written before 0011 must read back with no recorded disclosure");
            }
        }
    }

    private async Task EmbeddingsAsync()
    {
        foreach (var item in Items("embeddings"))
        {
            var (name, scope, id) = Identify(item);
            var scan = await _index.ScanAsync(
                Auth,
                new ExperienceIndexScan(scope, EmbeddingModel, Eligible, MinimumConfidence: 0, ExperienceIds: [id]),
                CancellationToken.None);
            var target = scan.Targets.SingleOrDefault(t => t.ExperienceId == id);
            _report.Check(target?.Stored is not null, $"embedding {name} ({id})", $"the scan found no stored embedding (outcome {scan.Outcome})");
            if (target is not null)
            {
                _report.Compare($"embedding {name} ({id})", item["target"], target);
            }

            // No API returns a stored vector, so it is read from the table, as the application role, and must be
            // exactly what the preview wrote. The read declares the record's tenant first, exactly as a store
            // declares its bounds, so it holds with row-level security on (story 15.1) as well as off.
            var vector = item["vector"].Deserialize<float[]>(UpgradeReport.Json)!;
            var stored = await RowLevelSecurityMode.DeclaredScalarAsync<float[]>(
                _context.Application,
                scope.TenantId,
                "SELECT embedding::real[] FROM agent_experience.experience_embeddings WHERE experience_id = @id",
                new NpgsqlParameter<Guid>("id", id));
            _report.Check(stored.SequenceEqual(vector), $"embedding {name} ({id})", $"the stored vector is [{string.Join(", ", stored)}], expected [{string.Join(", ", vector)}]");

            // And a search with it finds what it found before, at exactly the relevance it had.
            var search = await _index.SearchAsync(
                Auth,
                new ExperienceVectorQuery(scope, EmbeddingModel, vector, Eligible, MinimumConfidence: 0, Limit: 10),
                CancellationToken.None);
            _report.Check(search.Outcome == ExperienceVectorSearchOutcome.Found, $"embedding {name} ({id})", $"searching with its own vector was {search.Outcome}");
            _report.Compare(
                $"vector search with embedding {name} ({id})",
                item["searchHits"],
                search.Candidates.Select(c => new SearchHit(c.Record.ExperienceId, c.Relevance, c.SharedByGrant, c.PermittingGrantId)).ToList());
        }
    }

    /// <summary>In crypto-shredding mode every live row is sealed, and in plaintext mode none is.</summary>
    private async Task StorageModeAsync()
    {
        var (live, sealedRows, sealedSearch) = await CountsAsync(
            "SELECT count(*), count(*) FILTER (WHERE payload_version = 2), count(*) FILTER (WHERE search_vector_sealed IS NOT NULL) " +
            "FROM agent_experience.experience_records WHERE deleted_at IS NULL");
        var sealedRationales = await UpgradeFromPublishedPreviewsTests.ScalarAsync<long>(
            _context.Owner,
            "SELECT count(*) FROM agent_experience.reuse_feedback_exposures WHERE rationale_sealed IS NOT NULL");

        if (_context.Encrypted)
        {
            _report.Check(live > 0 && sealedRows == live && sealedSearch == live, "the sealed records", $"{sealedRows} of {live} live records are sealed, {sealedSearch} with a sealed search vector");
            _report.Check(sealedRationales > 0, "the sealed feedback rationale", "no exposure row carries a sealed rationale");
            foreach (var item in Items("records"))
            {
                var (name, scope, id) = Identify(item);
                var key = await new FileWrappedKeyRepository(_context.KeysPath!).GetAsync(new ExperienceKeyReference(id, scope), CancellationToken.None);
                _report.Check(key is { IsDestroyed: false }, $"record {name} ({id})", "its data key is missing or destroyed");
            }
        }
        else
        {
            _report.Check(live > 0 && sealedRows == 0 && sealedSearch == 0 && sealedRationales == 0, "the plaintext records", $"{sealedRows} of {live} live records, {sealedSearch} search vectors and {sealedRationales} rationales are sealed in plaintext mode");
        }
    }

    /// <summary>
    /// Story 14.4: <c>0021</c>'s backfill. Every live seeded row is unknown when the preview sealed it, and
    /// deterministic otherwise, and every tombstone carries the fixed false. The test-written plaintext copy that says
    /// <c>Model</c> is flagged and left out of an excluding search, and its untouched twin is not. Every text search the
    /// manifest lists answers the same with the exclusion on: the seeded records are deterministic, or (sealed) unknown
    /// and so kept by the source.
    /// </summary>
    private async Task AuthorshipAsync()
    {
        var (live, unknown, flagged) = await CountsAsync(
            "SELECT count(*), count(*) FILTER (WHERE reflection_model_authored IS NULL), count(*) FILTER (WHERE reflection_model_authored) " +
            "FROM agent_experience.experience_records WHERE deleted_at IS NULL");
        var tombstonesFlagged = await UpgradeFromPublishedPreviewsTests.ScalarAsync<long>(
            _context.Owner,
            "SELECT count(*) FROM agent_experience.experience_records WHERE deleted_at IS NOT NULL AND reflection_model_authored IS NOT FALSE");
        _report.Check(tombstonesFlagged == 0, "the authorship of the tombstones", $"{tombstonesFlagged} tombstones carry something but the fixed false");

        if (_context.Encrypted)
        {
            _report.Check(live > 0 && unknown == live && flagged == 0, "the sealed records' authorship", $"{unknown} of {live} live sealed records are unknown and {flagged} flagged; every one must be unknown");
        }
        else
        {
            _report.Check(live > 0 && unknown == 0 && flagged == 1, "the plaintext records' authorship", $"{unknown} of {live} live plaintext records are unknown and {flagged} flagged; expected none unknown and only the test's copy flagged");

            var scope = Record("a-validated")["scope"].Deserialize<Scope>(UpgradeReport.Json)! with { ProjectId = AuthorshipProject };
            var text = (string)Items("searches").First()["text"]!;
            var excluding = await _candidates.SearchAsync(
                Auth, new ExperienceCandidateQuery(scope, text, Eligible, MinimumConfidence: 0) { ExcludeModelAuthored = true }, CancellationToken.None);
            var including = await _candidates.SearchAsync(
                Auth, new ExperienceCandidateQuery(scope, text, Eligible, MinimumConfidence: 0), CancellationToken.None);
            _report.Check(
                excluding.Candidates.Select(c => c.Record.ExperienceId).SequenceEqual([AuthorshipTwinId])
                    && including.Candidates.Select(c => c.Record.ExperienceId).Order().SequenceEqual(new[] { AuthorshipModelId, AuthorshipTwinId }.Order())
                    && including.Candidates.Single(c => c.Record.ExperienceId == AuthorshipModelId).Record.Reflection?.Authorship == ReflectionAuthorship.Model,
                "the backfilled authorship",
                $"an excluding search returned [{string.Join(", ", excluding.Candidates.Select(c => c.Record.ExperienceId))}], expected only the twin; " +
                $"without the exclusion [{string.Join(", ", including.Candidates.Select(c => c.Record.ExperienceId))}]");
        }

        foreach (var item in Items("searches"))
        {
            var scope = item["scope"].Deserialize<Scope>(UpgradeReport.Json)!;
            var search = await _candidates.SearchAsync(
                Auth,
                new ExperienceCandidateQuery(scope, (string)item["text"]!, Eligible, MinimumConfidence: 0) { ExcludeModelAuthored = true },
                CancellationToken.None);
            _report.Compare($"excluding text search {item["name"]}", item["result"], search);
        }
    }

    // ------------------------------------------------------------------ writes

    private async Task TransitionAsync()
    {
        var validated = Record("a-validated");
        var (_, scope, id) = Identify(validated);
        var revision = (long)validated["revision"]!;
        var transition = await new ExperienceLifecycleService(_store).CommitAsync(
            Auth,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(),
                id,
                scope,
                ExperienceStatus.Validated,
                ExperienceStatus.Reinforced,
                "reinforced after the upgrade",
                "upgrade-tests",
                DateTimeOffset.UtcNow,
                revision),
            CancellationToken.None);
        var reinforced = await _store.GetAsync(Auth, scope, id, CancellationToken.None);
        _report.Check(
            transition.Outcome == LifecycleTransitionOutcome.Committed && reinforced.Record?.Status == ExperienceStatus.Reinforced && reinforced.Record.Revision == revision + 1,
            $"a new transition on a-validated ({id})",
            $"Validated to Reinforced was {transition.Outcome}; the record reads back {reinforced.Record?.Status} at revision {reinforced.Record?.Revision}");
    }

    private async Task EvidenceAsync()
    {
        var (_, scope, target) = Identify(Record("a-validated"));
        var contested = Record("a-contested")["result"].Deserialize<ExperienceRecordGetResult>(UpgradeReport.Json)!.Record!;
        var round = contested.Outcome.Evidence[0].VerificationRoundId;
        var verifying = new ExperienceLifecycleService(_store);

        // Refused: machine evidence about a-validated naming a-contested's run. Today's default lifecycle service
        // verifies independence, and that run vouches for nothing it can check (documented, CHANGELOG 0.1.0-preview.2
        // Breaking changes): a preview.1 record reads back HostWritten, and a preview.2 run was exposed to nothing.
        var expected = Preview1 ? IndependenceRefusal.HostWrittenRun : IndependenceRefusal.NotExposed;
        var refused = await verifying.ApplyEvidenceAsync(Auth, Evidence(target, scope, contested.SourceRunId, round), CancellationToken.None);
        _report.Check(
            refused.Outcome == ConfidenceUpdateOutcome.Unverified && refused.Refusal == expected,
            $"confidence evidence on a-validated ({target})",
            $"evidence naming a-contested's run must be Unverified with {expected}; it was {refused.Outcome} ({refused.Refusal}: {refused.Reason})");

        // The documented opt-out for such a deployment admits the same observation, labelled HostTrusted.
        var optedOut = new ExperienceLifecycleService(
            _store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });
        var trusted = Evidence(target, scope, contested.SourceRunId, round);
        var admitted = await optedOut.ApplyEvidenceAsync(Auth, trusted, CancellationToken.None);
        await LandedAsync("the opt-out's evidence on a-validated", scope, target, trusted.EvidenceId, admitted, ConfidenceEvidenceAdmission.HostTrusted);

        // Verified: preview.2 finalized a run it had exposed to a-validated, and named it in no evidence. Evidence
        // about that run passes today's verification.
        if (_context.Manifest["exposedRun"] is JsonObject exposed)
        {
            var exposedTarget = Guid.Parse((string)exposed["targetId"]!);
            var request = Evidence(
                exposedTarget,
                exposed["scope"].Deserialize<Scope>(UpgradeReport.Json)!,
                Guid.Parse((string)exposed["runId"]!),
                Guid.Parse((string)exposed["verificationRoundId"]!));
            var verified = await verifying.ApplyEvidenceAsync(Auth, request, CancellationToken.None);
            await LandedAsync("exposure-bound evidence on a-validated", request.Scope, exposedTarget, request.EvidenceId, verified, ConfidenceEvidenceAdmission.Verified);
        }
        else
        {
            _report.Check(Preview1, "the exposure-bound evidence", "a preview with exposure-bound evidence must seed an exposed run");
        }
    }

    private async Task LandedAsync(string item, Scope scope, Guid id, Guid evidenceId, ApplyConfidenceEvidenceResult result, ConfidenceEvidenceAdmission admission)
    {
        var history = await _store.GetHistoryAsync(Auth, new ExperienceRecordHistoryQuery(scope, id, Limit: ExperienceRecordHistoryQuery.MaxLimit), CancellationToken.None);
        var landed = history.Events.LastOrDefault()?.Event.Confidence;
        _report.Check(
            result.Outcome == ConfidenceUpdateOutcome.Applied && landed?.EvidenceId == evidenceId && landed.Admission == admission,
            item,
            $"it was {result.Outcome} ({result.Refusal}: {result.Reason}); the record's last event carries evidence {landed?.EvidenceId} admitted {landed?.Admission}, expected {admission}");
    }

    private static ApplyConfidenceEvidenceRequest Evidence(Guid target, Scope scope, Guid runId, Guid roundId) => new(
        Guid.NewGuid(),
        target,
        scope,
        Guid.NewGuid(),
        ConfidenceEvidenceKind.Supporting,
        ConfidenceEvidenceSource.Machine,
        runId,
        roundId,
        "supporting evidence after the upgrade",
        "upgrade-tests",
        DateTimeOffset.UtcNow);

    /// <summary>The grant the seeder issued to expire seconds later: once it has, it admits nothing and is purged.</summary>
    private async Task ExpiryAsync()
    {
        var expiring = _context.Manifest["expiringGrant"]!.AsObject();
        var grantId = Guid.Parse((string)expiring["grantId"]!);
        var id = Guid.Parse((string)expiring["experienceId"]!);
        var recordScope = expiring["recordScope"].Deserialize<Scope>(UpgradeReport.Json)!;
        var recipient = expiring["recipientScope"].Deserialize<Scope>(UpgradeReport.Json)!;
        var expiresAt = expiring["expiresAt"].Deserialize<DateTimeOffset>(UpgradeReport.Json);
        _report.Check(expiring["readWhileLive"]?["sharedByGrant"]?.GetValue<bool>() == true, $"expiring grant {grantId}", "the seeder's read through it was not a shared read");

        // The database's clock decides; allow it a little skew against this one.
        var wait = expiresAt.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        var read = await _store.GetAsync(Auth, recipient, id, CancellationToken.None);
        _report.Check(read.Outcome == ExperienceStoreOutcome.NotFound, $"expiring grant {grantId}", $"after it expired, the recipient's read was {read.Outcome}, expected NotFound");

        var purged = await _grants.PurgeExpiredAsync(Auth, Admin, recordScope, 100, CancellationToken.None);
        var history = await _grants.GetHistoryAsync(Auth, recordScope, grantId, CancellationToken.None);
        _report.Check(
            purged.Outcome == ExperienceStoreOutcome.Deleted && purged.PurgedCount == 1 && history.Outcome == ExperienceGrantOutcome.NotFound,
            $"expiring grant {grantId}",
            $"purging expired grants was {purged.Outcome} and removed {purged.PurgedCount}; the grant's history then reads {history.Outcome}, expected NotFound");
    }

    private async Task NewGrantAsync()
    {
        var (_, scope, id) = Identify(Record("a-validated"));
        var recipient = scope with { TeamId = "team-c" };
        var grantId = Guid.NewGuid();
        var created = await _grants.CreateAsync(
            Auth,
            Admin,
            new ExperienceGrantRequest(grantId, id, scope, recipient, "shared after the upgrade", DateTimeOffset.UtcNow.AddDays(7), ExperienceGrantDisclosure.LessonAndApproach),
            CancellationToken.None);
        var shared = await _store.GetAsync(Auth, recipient, id, CancellationToken.None);
        var revoked = await _grants.RevokeAsync(Auth, Admin, new ExperienceGrantRevocation(grantId, scope, "revoked after the upgrade"), CancellationToken.None);
        var afterRevoke = await _store.GetAsync(Auth, recipient, id, CancellationToken.None);
        var history = await _grants.GetHistoryAsync(Auth, scope, grantId, CancellationToken.None);
        _report.Check(
            created.Outcome == ExperienceGrantOutcome.Created
                && shared is { Outcome: ExperienceStoreOutcome.Found, PermittingGrantId: var permitting } && permitting == grantId
                && revoked.Outcome == ExperienceGrantOutcome.Revoked
                && afterRevoke.Outcome == ExperienceStoreOutcome.NotFound
                && history.Events.Select(e => e.Action).SequenceEqual([ExperienceGrantAction.Issued, ExperienceGrantAction.Revoked]),
            $"a new grant on a-validated ({id})",
            $"issuing was {created.Outcome}, the shared read {shared.Outcome}, revoking {revoked.Outcome}, the read after it {afterRevoke.Outcome}, and its history [{string.Join(", ", history.Events.Select(e => e.Action))}]");
    }

    private async Task ErasureAsync()
    {
        var (name, scope, id) = Identify(Record("a-superseded"));
        var deleted = await _store.DeleteAsync(Auth, scope, id, CancellationToken.None);
        var read = await _store.GetAsync(Auth, scope, id, CancellationToken.None);
        _report.Check(
            deleted.Outcome == ExperienceStoreOutcome.Deleted && read.Outcome == ExperienceStoreOutcome.Deleted,
            $"erasing {name} ({id})",
            $"DeleteAsync was {deleted.Outcome} and the record then reads {read.Outcome}; both must be Deleted");
        await RefusesRecreationAsync($"erasing {name} ({id})", scope, id);
        if (_context.Encrypted)
        {
            await KeyDestroyedAsync($"erasing {name} ({id})", scope, id);
        }
    }

    private async Task NewFeedbackAsync()
    {
        var (_, scope, id) = Identify(Record("a-validated"));
        var feedback = new RecordedExperienceReuseFeedback(
            FeedbackId: Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            Scope: scope,
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
            Measure: new ReuseMeasure("duration-seconds", 3),
            TrialLabel: "after-the-upgrade",
            ObservedAt: DateTimeOffset.UtcNow,
            Exposures: [new ExperienceReuseExposure(id, Attributed: false, EvidenceId: null)]);
        var recorded = await _feedback.RecordAsync(Auth, feedback, CancellationToken.None);
        _report.Check(recorded.Outcome == ExperienceReuseFeedbackStoreOutcome.Recorded, $"new reuse feedback naming a-validated ({id})", $"it was {recorded.Outcome}");
    }

    private async Task NewEmbeddingAsync()
    {
        var (_, scope, id) = Identify(Record("a-validated"));
        var record = (await _store.GetAsync(Auth, scope, id, CancellationToken.None)).Record!;
        var descriptor = new ExperienceEmbeddingDescriptor(
            EmbeddingModel,
            4,
            ExperienceEmbeddingDescriptor.ComputeContentHash(EmbeddingModel, ExperienceRetrievalSummary.For(record)),
            record.Revision);
        var written = await _index.WriteAsync(Auth, new ExperienceIndexWrite(scope, id, descriptor, new float[] { 0.8f, 0.6f, 0f, 0f }), CancellationToken.None);
        var scan = await _index.ScanAsync(Auth, new ExperienceIndexScan(scope, EmbeddingModel, Eligible, MinimumConfidence: 0, ExperienceIds: [id]), CancellationToken.None);
        _report.Check(
            written.Outcome == ExperienceIndexOutcome.Written && scan.Targets.SingleOrDefault()?.Stored == descriptor,
            $"a new embedding on a-validated ({id})",
            $"writing it at revision {record.Revision} was {written.Outcome}; the scan then reports {scan.Targets.SingleOrDefault()?.Stored}");
    }

    // ------------------------------------------------------------------ the catalog

    private async Task JournalAsync()
    {
        // The journal is the owner's: the application role holds nothing on it.
        var journaled = new List<string>();
        await using (var command = _context.Owner.CreateCommand("SELECT scriptname FROM agent_experience.schema_versions"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                journaled.Add(reader.GetString(0));
            }
        }

        var expected = PostgresExperienceRecordSchema.ScriptNames.Select(name => "AgentExperience.Storage.Postgres.Migrations." + name)
            .Concat(ExperienceVectorSchema.ScriptNames.Select(name => "AgentExperience.Storage.Postgres.Vectors.Migrations." + name))
            .Order(StringComparer.Ordinal)
            .ToList();
        var actual = journaled.Order(StringComparer.Ordinal).ToList();
        _report.Check(
            expected.SequenceEqual(actual),
            "the migration journal",
            $"it must list every current script exactly once; missing [{string.Join(", ", expected.Except(actual))}], " +
            $"unexpected or repeated [{string.Join(", ", actual.GroupBy(n => n).Where(g => g.Count() > 1 || !expected.Contains(g.Key)).Select(g => $"{g.Key} x{g.Count()}"))}]");
    }

    // ------------------------------------------------------------------ last

    private async Task FeedbackReplayAsync()
    {
        foreach (var item in Items("feedback"))
        {
            var name = (string)item["name"]!;
            var feedbackId = Guid.Parse((string)item["feedbackId"]!);

            // The ledger has no read port: a submission is read back by replaying it, which the store compares field by
            // field against what it holds and answers AlreadyRecorded, with the stored submission, only when every
            // field and exposure is identical. Recorded means the ledger had lost it (and the replay has now written
            // it again), which is why this runs after every other check.
            var submission = item["feedback"].Deserialize<RecordedExperienceReuseFeedback>(UpgradeReport.Json)!;
            var replayed = await _feedback.RecordAsync(Auth, submission, CancellationToken.None);
            var detail = replayed.Outcome == ExperienceReuseFeedbackStoreOutcome.Recorded
                ? "the ledger no longer held it: replaying it recorded it anew"
                : $"replaying it was {replayed.Outcome}, expected AlreadyRecorded{(replayed.Errors.Count > 0 ? ": " + string.Join(", ", replayed.Errors.Select(e => $"{e.Path} {e.Message}")) : string.Empty)}";
            _report.Check(replayed.Outcome == ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, $"reuse feedback {name} ({feedbackId})", detail);
            if (replayed.Outcome == ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded && replayed.Feedback is not null)
            {
                _report.Compare($"reuse feedback {name} ({feedbackId})", item["feedback"], replayed.Feedback);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task RefusesRecreationAsync(string item, Scope scope, Guid id)
    {
        var now = DateTimeOffset.UtcNow;
        var recreated = await _store.CreateAsync(
            Auth,
            new ExperienceRecord(
                ExperienceId: id,
                SourceRunId: Guid.NewGuid(),
                Scope: scope,
                TaskId: "recreated-after-the-upgrade",
                TaskSummary: null,
                Attempts: [],
                Outcome: new Outcome(TaskVerificationStatus.Unknown, [], null, now),
                CompletionScore: 0,
                Reflection: null,
                Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
                Provenance: new Provenance("upgrade-tests", null, now, null),
                Status: ExperienceStatus.Candidate,
                ReuseConfidence: 0,
                SupportingValidations: 0,
                Contradictions: 0,
                Revision: 0,
                CreatedAt: now,
                UpdatedAt: now),
            CancellationToken.None);
        _report.Check(
            recreated.Outcome == ExperienceStoreOutcome.Conflict,
            item,
            $"re-creating a record under its ID was {recreated.Outcome}, expected Conflict: an erased ID must never be reused");
    }

    private async Task KeyDestroyedAsync(string item, Scope scope, Guid id)
    {
        var key = await new FileWrappedKeyRepository(_context.KeysPath!).GetAsync(new ExperienceKeyReference(id, scope), CancellationToken.None);
        _report.Check(key is { IsDestroyed: true }, item, "the erasure did not destroy the record's data key");
    }

    private async Task<(long, long, long)> CountsAsync(string sql)
    {
        await using var command = _context.Owner.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private IEnumerable<JsonObject> Items(string section)
    {
        var items = OptionalItems(section).ToList();
        _report.Check(items.Count > 0, $"the manifest's {section}", "the seeder recorded none, so nothing of this kind was checked");
        return items;
    }

    private IEnumerable<JsonObject> OptionalItems(string section) =>
        _context.Manifest[section]?.AsArray().Select(node => node!.AsObject()).ToList() ?? [];

    private JsonObject Record(string name) => OptionalItems("records").Single(item => (string?)item["name"] == name);

    private static (string Name, Scope Scope, Guid Id) Identify(JsonObject item) => (
        (string)item["name"]!,
        item["scope"].Deserialize<Scope>(UpgradeReport.Json)!,
        Guid.Parse((string)item["experienceId"]!));

    private static List<Guid> Guids(JsonNode? node) => node.Deserialize<List<Guid>>(UpgradeReport.Json) ?? [];

    /// <summary>One vector search hit, as the seeder recorded it.</summary>
    private sealed record SearchHit(Guid ExperienceId, double Relevance, bool SharedByGrant, Guid? PermittingGrantId);
}
