namespace AgentExperience.Contract.Tests;

/// <summary>
/// The Python port's cross-language proof, the .NET half (story 22.3, read from the raw rows since story 22.8). A
/// Python MAF agent wrote two runs through the Python PostgreSQL store; the Python tests check that the rows it wrote,
/// every column it writes in PostgreSQL's own text form, are exactly <c>python/tests/cross-language/python-agent-rows.json</c>,
/// and that the records read back are exactly <c>python-agent-records.json</c>. <see cref="PortAgentRowsPostgres"/> inserts
/// those raw rows as they are, reads them back through the .NET store, checks each initial lifecycle event, compares
/// every column with what the .NET store writes for the same record, and has the .NET retrieval find the Python agent's
/// lessons for a similar task.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class PythonAgentRecordsPostgresRoundTripTests(ContractPostgresDatabase database)
{
    private static readonly string Directory = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "python", "tests", "cross-language");

    [Fact]
    public Task Every_row_the_python_agent_wrote_reads_back_through_the_store_unchanged_and_is_found_by_retrieval() =>
        PortAgentRowsPostgres.VerifyAsync(
            database,
            "python_agent",
            Path.Combine(Directory, "python-agent-rows.json"),
            Path.Combine(Directory, "python-agent-records.json"),
            "Ticket #5120: a refund is stuck on a lock. Triage it.");
}
