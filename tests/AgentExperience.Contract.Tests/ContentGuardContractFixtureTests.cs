namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.14's content-guard check: <c>cases.json</c> is what .NET's content guard answers for its corpus.</summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void The_content_guard_fixture_is_what_the_dotnet_guard_answers()
    {
        var drift = new List<string>();
        ContractFiles.Verify($"fixtures/{ContentGuardContract.Family}/{ContentGuardContract.FileName}", ContractJson.Write(ContentGuardContract.Run()), drift);
        ContractFiles.VerifyNoStray(ContentGuardContract.Family, [], [], drift, ContentGuardContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }
}
