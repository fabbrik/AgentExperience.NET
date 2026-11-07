using AgentExperience.CiTriage.GitHub;

namespace AgentExperience.CiTriage.Tests;

/// <summary>The no-hindsight tool path: the tools read only the failing commit, and only within their bounds.</summary>
public sealed class TriageToolTests
{
    private sealed class Repository : IRepository
    {
        public List<(string Sha, string? Path)> Reads { get; } = [];

        public Task<IReadOnlyList<string>?> ChangedFilesAsync(string sha, CancellationToken cancellationToken)
        {
            Reads.Add((sha, null));
            return Task.FromResult<IReadOnlyList<string>?>(["src/A.cs"]);
        }

        public Task<IReadOnlyList<string>?> DiffNamesAsync(string fromSha, string toSha, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The tools never diff.");

        public Task<string?> ShowFileAsync(string sha, string path, CancellationToken cancellationToken)
        {
            Reads.Add((sha, path));
            return Task.FromResult<string?>(path == "big.txt" ? new string('x', 20_000) : "small");
        }
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("HEAD:src/A.cs")]
    [InlineData("c:/windows")]
    [InlineData("  ")]
    public async Task Read_file_refuses_a_path_that_could_leave_the_failing_commit(string path)
    {
        var repository = new Repository();
        var workspace = new TriageWorkspace(repository) { FailSha = "fail" };

        Assert.StartsWith("(refused", await TriageAgent.ReadFileAsync(workspace, path, default), StringComparison.Ordinal);
        Assert.Empty(repository.Reads);
    }

    [Fact]
    public async Task Read_file_returns_at_most_8000_characters_of_the_failing_commit()
    {
        var repository = new Repository();
        var workspace = new TriageWorkspace(repository) { FailSha = "fail" };

        var text = await TriageAgent.ReadFileAsync(workspace, "./big.txt", default);

        Assert.StartsWith(new string('x', TriageAgent.MaxFileCharacters), text, StringComparison.Ordinal);
        Assert.Equal(TriageAgent.MaxFileCharacters, text.Count(c => c == 'x'));
        Assert.Equal([("fail", (string?)"big.txt")], repository.Reads);
    }

    [Fact]
    public async Task Both_tools_read_the_failing_sha_and_refuse_when_no_failure_is_set()
    {
        var repository = new Repository();
        var workspace = new TriageWorkspace(repository) { FailSha = "fail" };

        Assert.Equal("src/A.cs", await TriageAgent.ChangedFilesAsync(workspace, default));
        Assert.Equal("small", await TriageAgent.ReadFileAsync(workspace, "src/A.cs", default));
        Assert.All(repository.Reads, read => Assert.Equal("fail", read.Sha));

        workspace.FailSha = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriageAgent.ChangedFilesAsync(workspace, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TriageAgent.ReadFileAsync(workspace, "src/A.cs", default));
    }
}
