using System.Globalization;
using AgentExperience.RetrievalQuality.Harness;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.RetrievalQuality;

/// <summary>
/// Story 20.1: the retrieval benchmark over <see cref="InMemoryExperienceCandidateSource"/>, which matches whole words
/// with no stemming. It needs no Docker, so its golden is checked on every run.
/// </summary>
public sealed class InMemoryRetrievalQualityTests : RetrievalQualityTests
{
    protected override string Adapter => "in-memory candidate source";

    protected override string GoldenFileName => "GoldenInMemoryReport.txt";

    private protected override Task<RetrievalQualityResult> RunAsync(RetrievalCorpus corpus)
    {
        var store = new InMemoryExperienceRecordStore();
        return RetrievalQualityRun.RunAsync(Adapter, corpus, store, new InMemoryExperienceCandidateSource(store));
    }

    /// <summary>
    /// The determinism guarantee under three current cultures rather than only the machine's own: Turkish case-folds
    /// "I" differently, and German writes a decimal comma.
    /// </summary>
    /// <param name="cultureName">The culture to force for the duration of the run, or empty for the invariant culture.</param>
    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task The_report_renders_the_same_bytes_under_any_culture(string cultureName)
    {
        var culture = cultureName.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            rendered = RetrievalQualityReport.Render(await RunAsync(RetrievalCorpus.Load()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        Assert.Equal(GoldenFile.Read(GoldenFileName), rendered);
    }
}
