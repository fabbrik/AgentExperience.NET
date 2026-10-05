using System.Text;
using Npgsql;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// Waits, deterministically, until a statement is actually parked on a lock another connection holds
/// (story 19.3). A race test that only sleeps and hopes the statement got there silently stops testing the
/// race on a loaded machine; this one either observes the wait or fails the test.
/// </summary>
/// <remarks>
/// Compiled into each test project that starts a container (linked, not referenced, so no test project
/// gains a dependency on another). The signal is <c>pg_blocking_pids</c>: a backend whose current statement
/// contains the given fragment and that the holding connection's backend is blocking. Naming the holder
/// keeps it exact even when other tests share the container.
/// </remarks>
internal static class PostgresLockWait
{
    /// <summary>How long to wait before failing the test. Long, because it only bounds a failure.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns once a backend running a statement that contains <paramref name="statementFragment"/> is
    /// waiting on a lock held by <paramref name="holder"/>; fails the test if none is by the deadline.
    /// </summary>
    /// <param name="observer">A data source to poll from; never the holder, whose connection is busy holding.</param>
    /// <param name="holder">The open connection whose transaction holds the lock.</param>
    /// <param name="statementFragment">Text the blocked statement contains, e.g. <c>purge_experience_record</c>.</param>
    /// <param name="competing">
    /// The operation expected to block. If it finishes first, its own exception surfaces, or the test fails
    /// saying it finished without blocking, instead of polling to the deadline.
    /// </param>
    internal static async Task UntilBlockedAsync(NpgsqlDataSource observer, NpgsqlConnection holder, string statementFragment, Task competing)
    {
        var holderPid = holder.ProcessID;
        var deadline = DateTimeOffset.UtcNow + Deadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (competing.IsCompleted)
            {
                await competing;
                Xunit.Assert.Fail(
                    $"The competing operation finished without blocking on backend {holderPid}; " +
                    $"the race this test exists for did not happen.");
            }

            await using var command = observer.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity " +
                "WHERE wait_event_type = 'Lock' AND @holder = ANY(pg_blocking_pids(pid)) " +
                "AND strpos(query, @fragment) > 0");
            command.Parameters.Add(new NpgsqlParameter<int>("holder", holderPid));
            command.Parameters.Add(new NpgsqlParameter<string>("fragment", statementFragment));
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        Xunit.Assert.Fail(
            $"No statement containing '{statementFragment}' blocked on backend {holderPid} within {Deadline.TotalSeconds:0} s. " +
            $"Active backends: {await DescribeActivityAsync(observer)}");
    }

    private static async Task<string> DescribeActivityAsync(NpgsqlDataSource observer)
    {
        await using var command = observer.CreateCommand(
            "SELECT pid, coalesce(wait_event_type, ''), pg_blocking_pids(pid)::text, coalesce(left(query, 120), '') " +
            "FROM pg_stat_activity WHERE state <> 'idle' AND pid <> pg_backend_pid()");
        await using var reader = await command.ExecuteReaderAsync();
        var description = new StringBuilder();
        while (await reader.ReadAsync())
        {
            description.Append($"[{reader.GetInt32(0)} {reader.GetString(1)} blocked by {reader.GetString(2)}: {reader.GetString(3)}] ");
        }

        return description.Length == 0 ? "none" : description.ToString();
    }
}
