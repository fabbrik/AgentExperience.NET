namespace AgentExperience.Contract.Tests;

/// <summary>The version-bump rule of <see cref="ContractLock.Classify"/>: a minor version only adds files.</summary>
public sealed class ContractLockTests
{
    private static readonly Dictionary<string, string> Previous = new(StringComparer.Ordinal)
    {
        ["README.md"] = "r",
        ["schema/experience-record.v1.json"] = "s",
        ["sql/declare-bounds.sql"] = "q",
        ["fixtures/error-class/cases.json"] = "f",
    };

    private static Dictionary<string, string> With(Action<Dictionary<string, string>> change)
    {
        var files = new Dictionary<string, string>(Previous, StringComparer.Ordinal);
        change(files);
        return files;
    }

    [Fact]
    public void A_minor_bump_that_only_adds_files_is_accepted()
    {
        var current = With(files =>
        {
            files["README-1.1.md"] = "n";
            files["fixtures/encoding/cases.json"] = "e";
        });
        Assert.True(ContractLock.Classify("1.0", Previous, "1.1", current).Allowed);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("schema/experience-record.v1.json")]
    [InlineData("sql/declare-bounds.sql")]
    [InlineData("fixtures/error-class/cases.json")]
    public void A_minor_bump_that_changes_or_deletes_a_locked_file_is_refused(string name)
    {
        var changed = ContractLock.Classify("1.0", Previous, "1.1", With(files => files[name] = "changed"));
        Assert.False(changed.Allowed);
        Assert.Contains(name, changed.Reason, StringComparison.Ordinal);

        var deleted = ContractLock.Classify("1.0", Previous, "1.1", With(files => files.Remove(name)));
        Assert.False(deleted.Allowed);
        Assert.Contains(name, deleted.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("schema/experience-record.v1.json")]
    [InlineData("sql/declare-bounds.sql")]
    [InlineData("fixtures/error-class/cases.json")]
    public void A_major_bump_accepts_any_change(string name)
    {
        Assert.True(ContractLock.Classify("1.1", Previous, "2.0", With(files => files[name] = "changed")).Allowed);
        Assert.True(ContractLock.Classify("1.1", Previous, "2.0", With(files => files.Remove(name))).Allowed);
    }

    [Theory]
    [InlineData("1.1", "1.1")]
    [InlineData("1.1", "1.0")]
    [InlineData("2.0", "1.5")]
    [InlineData("1.0", "x")]
    public void A_version_that_is_not_later_is_refused(string previous, string next) =>
        Assert.False(ContractLock.Classify(previous, Previous, next, Previous).Allowed);
}
