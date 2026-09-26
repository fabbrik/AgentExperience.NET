using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class PreregistrationTests
{
    /// <summary>
    /// The git blob id of <c>preregistration.json</c> as it stands, and how many amendments it records. Any change to the
    /// file fails this test until both are updated here, and a changed file with no new amendment entry fails whatever
    /// this constant says: editing the pre-registration is always a visible act in review.
    /// </summary>
    private const string RegisteredBlobId = "238d644d79c256c79f73f8025fdc7b41cd672b5f";

    private const int RegisteredAmendments = 0;

    private static string OnDisk => Path.Combine(TestSupport.ExperimentDirectory(), "preregistration.json");

    [Fact]
    public void The_embedded_pre_registration_is_the_checked_in_file_byte_for_byte()
    {
        var bytes = File.ReadAllBytes(OnDisk);
        var embedded = LivePreregistration.ReadEmbedded();
        Assert.Equal(LivePreregistration.ComputeGitBlobId(bytes), embedded.GitBlobId);
        Assert.Equal(bytes.Length, embedded.ByteCount);
    }

    [Fact]
    public void A_changed_pre_registration_must_record_an_amendment()
    {
        var design = LivePreregistration.ReadEmbedded();
        Assert.True(
            design.GitBlobId == RegisteredBlobId,
            $"preregistration.json changed (blob {design.GitBlobId}, registered {RegisteredBlobId}). Record an entry in 'amendments' and update RegisteredBlobId and RegisteredAmendments.");
        Assert.True(design.Amendments.Count == RegisteredAmendments, $"{design.Amendments.Count} amendments recorded; {RegisteredAmendments} registered.");
    }

    [Fact]
    public void The_git_blob_id_is_what_git_hash_object_prints()
    {
        // `printf 'hello' | git hash-object --stdin`
        Assert.Equal("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0", LivePreregistration.ComputeGitBlobId(Encoding.ASCII.GetBytes("hello")));
    }

    [Fact]
    public void The_file_fixes_the_design_the_harness_runs()
    {
        var design = LivePreregistration.ReadEmbedded();

        design.Check(MigrationTaskSet.Current);
        Assert.Equal(MigrationTaskSet.CurrentVersion, design.TaskSetVersion);
        Assert.Equal(12, design.Instances);
        Assert.Equal(MigrationTaskSet.Current.AssignmentDigest(), design.HiddenAssignmentSha256);
        Assert.Equal("failed_attempts", design.PrimaryMetric);
        Assert.Equal(0.05, design.Alpha);
        Assert.Equal(0f, design.Temperature);
        Assert.Equal(6, design.EvaluationAttemptLimit);
        Assert.Equal(RolloutStrategies.All.Count, design.EvaluationAttemptLimit);
        Assert.Equal(["memory-disabled", "memory-enabled", "negative-control"], [design.ControlLabel, design.TreatmentLabel, design.NegativeControlLabel]);
        Assert.Contains(design.Alpha.ToString(System.Globalization.CultureInfo.InvariantCulture), design.GateExpression, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_set_whose_answers_changed_is_refused_before_any_model_call()
    {
        var instances = MigrationTaskSet.Current.Instances.ToList();
        instances[0] = instances[0] with { HiddenStrategy = instances[1].HiddenStrategy, StaleStrategy = instances[1].StaleStrategy };
        instances[1] = instances[1] with { HiddenStrategy = MigrationTaskSet.Current.Instances[0].HiddenStrategy, StaleStrategy = MigrationTaskSet.Current.Instances[0].StaleStrategy };
        var swapped = MigrationTaskSet.ForTests(MigrationTaskSet.CurrentVersion, instances);
        swapped.Validate();

        var model = new ScriptedOperatorModel();
        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => TestSupport.RunScriptedAsync(model, taskSet: swapped));
        Assert.Contains("digests to", refused.Message, StringComparison.Ordinal);
        Assert.Empty(model.Calls);
    }

    [Theory]
    [InlineData("primaryMetric", "tool_calls", "primary metric")]
    [InlineData("taskSetVersion", "live-reuse-migrations@2", "task set")]
    [InlineData("instances", 11, "instances")]
    public void A_pre_registration_the_harness_cannot_execute_is_refused(string field, object value, string expected)
    {
        var node = JsonNode.Parse(File.ReadAllText(OnDisk))!;
        node[field] = JsonValue.Create(value);
        var design = LivePreregistration.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        var refused = Assert.Throws<PreregistrationException>(() => design.Check(MigrationTaskSet.Current));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_field_is_refused_rather_than_defaulted()
    {
        var node = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        node.Remove("statisticalTest");
        Assert.Throws<PreregistrationException>(() => LivePreregistration.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }
}
