using AgentExperience.CiTriage.GitHub;

namespace AgentExperience.CiTriage.Tests;

public sealed class ScoringTests
{
    private static readonly CiRun Green = new(2, "feat/x", "b", "success", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    private static FailureLabel Code(params string[] files) => new(LabelKind.Code, files, Green);

    private static readonly FailureLabel Flaky = new(LabelKind.Flaky, [], Green);

    [Fact]
    public void Code_failure_with_a_right_file_among_the_predicted_is_correct() =>
        Assert.True(Scoring.IsCorrect(Code("a.cs"), new TriagePrediction("code", ["a.cs", "b.cs"])));

    [Fact]
    public void Code_failure_with_only_wrong_files_is_wrong() =>
        Assert.False(Scoring.IsCorrect(Code("a.cs"), new TriagePrediction("code", ["c.cs"])));

    [Fact]
    public void Code_failure_called_flaky_is_wrong() =>
        Assert.False(Scoring.IsCorrect(Code("a.cs"), new TriagePrediction("flaky", [])));

    [Fact]
    public void Only_the_first_five_predicted_files_count() =>
        Assert.False(Scoring.IsCorrect(Code("f.cs"), new TriagePrediction("code", ["a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs"])));

    [Fact]
    public void Flaky_failure_is_correct_only_when_called_flaky()
    {
        Assert.True(Scoring.IsCorrect(Flaky, new TriagePrediction("flaky", [])));
        Assert.False(Scoring.IsCorrect(Flaky, new TriagePrediction("code", ["a.cs"])));
    }

    [Fact]
    public void An_unparsed_reply_is_never_correct() => Assert.False(Scoring.IsCorrect(Code("a.cs"), null));

    [Theory]
    [InlineData(LabelKind.Ambiguous)]
    [InlineData(LabelKind.Pending)]
    [InlineData(LabelKind.Unavailable)]
    public void Ambiguous_pending_and_unavailable_failures_are_never_scored(LabelKind kind) =>
        Assert.Throws<ArgumentException>(() => Scoring.IsCorrect(new FailureLabel(kind, [], null), new TriagePrediction("flaky", [])));

    [Fact]
    public void A_reply_wrapped_in_prose_or_a_fence_parses_and_paths_are_normalised()
    {
        var prediction = Scoring.Parse("Here you go:\n```json\n{\"kind\": \"Code\", \"files\": [\"./src/a.cs\", \"/src/a.cs\", \"src\\\\b.cs\", \"\"]}\n```");
        Assert.NotNull(prediction);
        Assert.Equal("code", prediction.Kind);
        Assert.Equal(["src/a.cs", "src/b.cs"], prediction.Files);
    }

    [Fact]
    public void At_most_five_files_are_kept() =>
        Assert.Equal(5, Scoring.Parse("{\"kind\":\"code\",\"files\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"]}")!.Files.Count);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I think it is src/a.cs")]
    [InlineData("{\"kind\": \"maybe\", \"files\": []}")]
    [InlineData("{\"files\": [\"a.cs\"]}")]
    [InlineData("{not json}")]
    public void A_reply_without_the_object_is_unparsed(string? reply) => Assert.Null(Scoring.Parse(reply));

    [Fact]
    public void Flaky_drops_any_files() => Assert.Empty(Scoring.Parse("{\"kind\":\"flaky\",\"files\":[\"a.cs\"]}")!.Files);
}
