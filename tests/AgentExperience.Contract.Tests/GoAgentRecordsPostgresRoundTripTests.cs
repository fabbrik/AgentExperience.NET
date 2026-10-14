namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 22.6's cross-language proof, the .NET half. A Go MAF agent wrote two runs through the Go PostgreSQL store;
/// the Go tests check that the rows it wrote, every column it writes in PostgreSQL's own text form, are exactly
/// <c>go/maf/testdata/cross-language/go-agent-rows.json</c>, and that the records read back are exactly
/// <c>go-agent-records.json</c>. <see cref="PortAgentRowsPostgres"/> inserts those raw rows as they are, reads them back through
/// the .NET store, checks each initial lifecycle event, compares every column with what the .NET store writes for the
/// same record, and has the .NET retrieval find the Go agent's lesson for a similar task.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class GoAgentRecordsPostgresRoundTripTests(ContractPostgresDatabase database)
{
    private static readonly string Directory = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "go", "maf", "testdata", "cross-language");

    [Fact]
    public Task Every_row_the_go_agent_wrote_reads_back_through_the_store_unchanged_and_is_found_by_retrieval() =>
        PortAgentRowsPostgres.VerifyAsync(
            database,
            "go_agent",
            Path.Combine(Directory, "go-agent-rows.json"),
            Path.Combine(Directory, "go-agent-records.json"),
            "Ticket #5120: a refund is stuck on a lock. Triage it.");
}
