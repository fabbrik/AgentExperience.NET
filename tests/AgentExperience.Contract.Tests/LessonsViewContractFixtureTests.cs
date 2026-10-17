using System.Reflection;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.7's checks that need no database: the published statements are the .NET store's, line for line.</summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void The_lessons_view_statements_are_the_ones_the_store_runs()
    {
        var statements = VectorStatements.Read(LessonsViewContract.File);
        Assert.Equal(
            new[] { "page", "page-with-statuses", "page-without-injections", "page-without-injections-with-statuses" },
            statements.Keys.ToArray());

        var statusPredicate = StoreConstant("QueryStatusPredicate");
        var plain = StoreConstant("QuerySql");
        var orderAndLimit = StoreConstant("QueryOrderAndLimit");
        Assert.Equal(
            PostgresExperienceRecordStore.QueryWithInjectionsPrefix + PostgresExperienceRecordStore.QueryWithInjectionsSuffix,
            OneLine(statements["page"]));
        Assert.Equal(
            PostgresExperienceRecordStore.QueryWithInjectionsPrefix + statusPredicate + PostgresExperienceRecordStore.QueryWithInjectionsSuffix,
            OneLine(statements["page-with-statuses"]));
        Assert.Equal(plain + orderAndLimit, OneLine(statements["page-without-injections"]));
        Assert.Equal(plain + statusPredicate + orderAndLimit, OneLine(statements["page-without-injections-with-statuses"]));
    }

    private static string StoreConstant(string name) =>
        (string)typeof(PostgresExperienceRecordStore)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
}
