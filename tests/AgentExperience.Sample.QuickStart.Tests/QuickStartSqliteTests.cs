using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Data.Sqlite;

namespace AgentExperience.Sample.QuickStart.Tests;

/// <summary>
/// Story 28.7: the quick-start demo with <c>--sqlite &lt;path&gt;</c>, one run per process. Two calls, each building its own
/// container and model as two processes would, share only the file: the first learns, and the second is handed the
/// lesson the first stored and fails less.
/// </summary>
public sealed class QuickStartSqliteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quickstart-{Guid.NewGuid():N}", "experience.db");

    [Fact]
    public async Task A_second_process_over_the_same_file_is_handed_the_first_ones_lesson()
    {
        using var firstModel = new ScriptedModel();
        var first = await global::QuickStart.RunSqliteAsync(firstModel, global::QuickStart.StandInLabel, TextWriter.Null, _path);

        var output = new StringWriter();
        using var secondModel = new ScriptedModel();
        var second = await global::QuickStart.RunSqliteAsync(secondModel, global::QuickStart.StandInLabel, output, _path);

        Assert.Null(first.Block);
        Assert.True(first.FailedAttempts >= 1);
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, Assert.IsType<string>(second.Block), StringComparison.Ordinal);
        Assert.Equal(0, second.FailedAttempts);
        Assert.Equal("wait-for-lock", Assert.Single(second.Attempts).Strategy);
        Assert.Contains("lesson injected from the file", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
    }
}
