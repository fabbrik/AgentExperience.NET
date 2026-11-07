namespace AgentExperience.Contract.Tests;

/// <summary>
/// The opt-in a cross-language test needs, because it runs the Python and Go toolchains too. <c>1</c> runs it and
/// <c>0</c> skips it on purpose. Unset, it skips on a developer's machine but fails in CI (<c>CI=true</c> or
/// <c>GITHUB_ACTIONS</c> set): there a skipped test would count as a pass, so every job says which it means.
/// </summary>
internal static class CrossLanguageOptIn
{
    /// <summary>Whether the test guarded by <paramref name="variable"/> runs.</summary>
    public static bool Enabled(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value == "1")
        {
            return true;
        }

        if (value is null
            && (Environment.GetEnvironmentVariable("CI") == "true" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
        {
            Assert.Fail($"{variable} is not set in CI: set it to 1 to run this cross-language test, or to 0 to skip it on purpose.");
        }

        return false;
    }
}
