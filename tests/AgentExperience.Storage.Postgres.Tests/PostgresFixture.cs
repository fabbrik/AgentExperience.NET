using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Starts one ephemeral <c>pgvector/pgvector:pg16</c> container for the whole collection and sets up the
/// supported <b>two-role deployment</b> in it: a non-superuser owner role owns a fresh database and runs
/// <see cref="ExperienceSchemaMigrator"/>, and a separate application role is given exactly the stores'
/// privileges by <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync"/> (with both
/// purge opt-ins). <see cref="DataSource"/> connects as the <em>application</em> role, so every store test
/// in this project runs as the role a production host runs as. A test that deliberately acts as the owner
/// -- tampering to prove a trigger binds a writer that holds the privilege, DDL, backdating a row -- says
/// so by using <see cref="OwnerDataSource"/>. Migrator tests create their own databases in the same
/// container through <see cref="CreateDatabaseAsync"/>. Set <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if
/// Ryuk fails under a local Docker setup.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The owner role: owns the fixture database and everything the migrator creates in it.</summary>
    public const string OwnerRoleName = "aen_fixture_owner";

    /// <summary>The application role: owns nothing, holds exactly the manifest.</summary>
    public const string ApplicationRoleName = "aen_fixture_app";

    /// <summary>The fixture database.</summary>
    public const string StoreDatabase = "aen_fixture_store";

    private const string RolePassword = "aen-role-password";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _superuser;
    private NpgsqlDataSource? _superuserInStore;
    private NpgsqlDataSource? _owner;
    private NpgsqlDataSource? _dataSource;

    /// <summary>The fixture database, connecting as the application role.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>The fixture database, connecting as the owner role that ran the migrator.</summary>
    public NpgsqlDataSource OwnerDataSource => _owner ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>The fixture database, connecting as the container's superuser.</summary>
    public NpgsqlDataSource SuperuserDataSource => _superuserInStore ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
        await _container.StartAsync();
        _superuser = NpgsqlDataSource.Create(_container.GetConnectionString());

        foreach (var sql in new[]
        {
            $"CREATE ROLE {OwnerRoleName} LOGIN PASSWORD '{RolePassword}'",
            $"CREATE ROLE {ApplicationRoleName} LOGIN PASSWORD '{RolePassword}'",
            $"CREATE DATABASE {StoreDatabase} OWNER {OwnerRoleName}",
            OwnerParameterGrant(OwnerRoleName),
        })
        {
            await using var command = _superuser.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        _superuserInStore = NpgsqlDataSource.Create(ConnectionString(StoreDatabase, username: null));
        _owner = NpgsqlDataSource.Create(ConnectionString(StoreDatabase, OwnerRoleName));
        _dataSource = NpgsqlDataSource.Create(ConnectionString(StoreDatabase, ApplicationRoleName));

        await ExperienceSchemaMigrator.MigrateAsync(_owner, CancellationToken.None);
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _owner,
            new ExperienceApplicationRoleOptions(ApplicationRoleName) { AllowErasure = true, AllowAccessLogPurge = true },
            CancellationToken.None);
    }

    /// <summary>
    /// The one statement a superuser must run before a non-superuser owner can migrate: <c>0010</c> and
    /// <c>0012</c> create functions with a <c>SET</c> clause for the two purge markers, and PostgreSQL 15+
    /// lets a non-superuser name a custom placeholder parameter there only when granted <c>SET</c> on it.
    /// Exactly the statement the store README's two-role deployment section prints.
    /// </summary>
    public static string OwnerParameterGrant(string ownerRole) =>
        "GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized " +
        $"TO \"{ownerRole}\"";

    /// <summary>
    /// A connection string to <paramref name="database"/> as <paramref name="username"/>, or as the
    /// container's superuser when it is <see langword="null"/>. Every role this fixture creates shares one
    /// password.
    /// </summary>
    public string ConnectionString(string database, string? username)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database };
        if (username is not null)
        {
            builder.Username = username;
            builder.Password = RolePassword;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Runs one statement as the container's superuser, in <paramref name="database"/> (the container's
    /// default database when <see langword="null"/>). For cluster-level setup: roles and databases.
    /// </summary>
    public async Task ExecuteAsSuperuserAsync(string sql, string? database = null)
    {
        if (database is null)
        {
            await using var command = _superuser!.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
            return;
        }

        await using var source = NpgsqlDataSource.Create(ConnectionString(database, username: null));
        await using var inDatabase = source.CreateCommand(sql);
        await inDatabase.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates a login role in the shared container with only the privileges <paramref name="grants"/>
    /// names, and returns a data source connecting as it to the fixture database. The caller owns the data
    /// source and disposes it; the role goes away with the container.
    /// </summary>
    /// <remarks>
    /// For privilege tests -- proving that a role which is <em>not</em> the owner cannot reach an
    /// operation, which the owning fixture connection can never prove about itself.
    /// </remarks>
    /// <param name="purpose">A short name fragment: 1 to 20 lower-case ASCII letters, digits, or underscores.</param>
    /// <param name="grants">Statements to run as the superuser in the fixture database after the role exists, each naming the role as <c>{role}</c>.</param>
    public async Task<NpgsqlDataSource> CreateRoleAsync(string purpose, params string[] grants)
    {
        var name = await CreateLoginRoleAsync(purpose);

        foreach (var grant in grants)
        {
            await using var command = SuperuserDataSource.CreateCommand(grant.Replace("{role}", $"\"{name}\"", StringComparison.Ordinal));
            await command.ExecuteNonQueryAsync();
        }

        return NpgsqlDataSource.Create(ConnectionString(StoreDatabase, name));
    }

    /// <summary>
    /// Creates a login role in the shared container, with nothing granted, and returns its name. Every
    /// fixture role shares one password, so <see cref="ConnectionString"/> connects as it.
    /// </summary>
    /// <param name="purpose">A short name fragment: 1 to 20 lower-case ASCII letters, digits, or underscores.</param>
    public async Task<string> CreateLoginRoleAsync(string purpose)
    {
        Assert.InRange(purpose.Length, 1, 20);
        Assert.All(purpose, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', $"Invalid purpose character '{c}'."));

        var name = $"aen_{purpose}_{Guid.NewGuid():N}";

        // CREATE ROLE takes no parameters, so the identifier is interpolated; every character of it has
        // just been checked against the allowlist above.
        await ExecuteAsSuperuserAsync($"CREATE ROLE \"{name}\" LOGIN PASSWORD '{RolePassword}'");
        return name;
    }

    /// <summary>
    /// Creates an empty database in the shared container and returns a data source for it, connecting as
    /// the container's superuser. The caller owns the data source and disposes it; the database goes away
    /// with the container.
    /// </summary>
    /// <param name="purpose">
    /// A short name fragment identifying the test: 1 to 20 lower-case ASCII letters, digits, or
    /// underscores. The bound keeps the generated identifier inside PostgreSQL's 63-byte limit, so two
    /// tests can never be truncated onto the same database.
    /// </param>
    /// <param name="configure">
    /// Optional: adjusts the data source builder before it is built -- for example to give it a host's
    /// logger factory.
    /// </param>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(string purpose, Action<NpgsqlDataSourceBuilder>? configure = null)
    {
        var name = await CreateDatabaseNameAsync(purpose, owner: null);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString(name, username: null));
        configure?.Invoke(dataSourceBuilder);
        return dataSourceBuilder.Build();
    }

    /// <summary>
    /// Creates an empty database in the shared container, owned by <paramref name="owner"/> (the superuser
    /// when <see langword="null"/>), and returns its name.
    /// </summary>
    public async Task<string> CreateDatabaseNameAsync(string purpose, string? owner)
    {
        Assert.InRange(purpose.Length, 1, 20);
        Assert.All(purpose, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', $"Invalid purpose character '{c}'."));

        // "aen_" + <=20 + "_" + 32 hex = at most 57 bytes, inside PostgreSQL's 63-byte identifier limit.
        var name = $"aen_{purpose}_{Guid.NewGuid():N}";

        // CREATE DATABASE takes no parameters and cannot run inside a transaction, so the identifier is
        // interpolated. Every character of it has just been checked against the allowlist above, and an
        // owner is always a name this fixture generated.
        await ExecuteAsSuperuserAsync(owner is null ? $"CREATE DATABASE \"{name}\"" : $"CREATE DATABASE \"{name}\" OWNER \"{owner}\"");
        return name;
    }

    public async Task DisposeAsync()
    {
        foreach (var source in new[] { _dataSource, _owner, _superuserInStore, _superuser })
        {
            if (source is not null)
            {
                await source.DisposeAsync();
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
