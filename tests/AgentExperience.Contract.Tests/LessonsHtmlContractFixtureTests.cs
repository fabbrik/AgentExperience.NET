using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.14's lessons-html checks: every fixture is the document <see cref="ExperienceLessonsHtml.Render"/> writes.</summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_lessons_html_fixture_is_the_document_the_renderer_writes()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in LessonsHtmlContract.Cases)
        {
            var directory = $"fixtures/{LessonsHtmlContract.Family}/{name}";
            var pageJson = ContractJson.Write(LessonsHtmlContract.PageJson(definition));
            ContractFiles.Verify($"{directory}/page.json", pageJson, drift);

            // From the page as a port reads it, which must be the page itself.
            var page = LessonsHtmlContract.ReadPage(ContractFiles.Read($"{directory}/page.json") ?? pageJson);
            var html = ExperienceLessonsHtml.Render(page);
            Assert.Equal(ExperienceLessonsHtml.Render(definition.Page()), html);
            ContractFiles.Verify($"{directory}/expected.html", html, drift);
        }

        ContractFiles.VerifyNoStray(LessonsHtmlContract.Family, LessonsHtmlContract.Cases.Select(entry => entry.Name), ["page.json", "expected.html"], drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }
}
