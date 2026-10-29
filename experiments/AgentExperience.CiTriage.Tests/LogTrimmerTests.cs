namespace AgentExperience.CiTriage.Tests;

public sealed class LogTrimmerTests
{
    private static string Lines(int count, string prefix) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"2026-10-27T05:50:{i % 60:00}.1234567Z {prefix} line {i}\n"));

    [Fact]
    public void Timestamps_and_colours_are_removed_and_the_error_region_kept()
    {
        var log = Lines(50, "noise")
            + "2026-10-27T05:50:23.3836487Z \u001b[31m  Failed AgentExperience.Tests.SomeTest [743 ms]\u001b[0m\n"
            + Lines(50, "after");

        var trimmed = LogTrimmer.Trim([new JobLog("build-and-test", log)]);

        Assert.StartsWith("=== job: build-and-test ===\n", trimmed, StringComparison.Ordinal);
        Assert.Contains("  Failed AgentExperience.Tests.SomeTest [743 ms]", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-27T", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", trimmed, StringComparison.Ordinal);
        Assert.Contains("noise line 49", trimmed, StringComparison.Ordinal);            // lines before
        Assert.Contains("after line 11", trimmed, StringComparison.Ordinal);            // lines after
        Assert.DoesNotContain("after line 12", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("noise line 10", trimmed, StringComparison.Ordinal);
        Assert.Contains(LogTrimmer.Gap, trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_with_no_error_line_keeps_its_tail()
    {
        var trimmed = LogTrimmer.Trim([new JobLog("job", Lines(100, "plain"))]);
        Assert.Contains("plain line 99", trimmed, StringComparison.Ordinal);
        Assert.Contains("plain line 60", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("plain line 59", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_echoed_script_is_not_an_error()
    {
        Assert.False(LogTrimmer.IsError("[command]/usr/bin/git config --get error.handler"));
        Assert.True(LogTrimmer.IsError("src/A.cs(1,2): error CS0103: The name 'x' does not exist"));
        Assert.True(LogTrimmer.IsError("##[error]Process completed with exit code 1."));
    }

    [Fact]
    public void The_whole_is_capped_and_shared_between_jobs()
    {
        var huge = string.Concat(Enumerable.Range(0, 5000).Select(i => $"error number {i}\n"));
        var trimmed = LogTrimmer.Trim([new JobLog("one", huge), new JobLog("two", huge)]);

        Assert.True(trimmed.Length <= LogTrimmer.MaxCharacters, $"{trimmed.Length} characters");
        Assert.Contains("=== job: one ===", trimmed, StringComparison.Ordinal);
        Assert.Contains("=== job: two ===", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void Headlines_are_specific_distinct_error_lines()
    {
        var log = "##[error]Process completed with exit code 1.\nerror CS0103: The name 'x' does not exist\nerror CS0103: The name 'x' does not exist\n  Failed Some.Test [1 ms]\nerror third\nerror fourth\n";
        Assert.Equal(["error CS0103: The name 'x' does not exist", "Failed Some.Test [1 ms]", "error third"], LogTrimmer.Headlines([new JobLog("j", log)]));
    }
}
