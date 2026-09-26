using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class TaskSetTests
{
    private static IReadOnlyList<MigrationInstance> Instances => MigrationTaskSet.Current.Instances;

    [Fact]
    public void The_current_task_set_is_valid()
    {
        MigrationTaskSet.Current.Validate();
        Assert.Equal(12, Instances.Count);
    }

    [Fact]
    public void Every_strategy_is_the_hidden_answer_twice_and_the_stale_answer_twice()
    {
        foreach (var strategy in RolloutStrategies.All)
        {
            Assert.Equal(2, Instances.Count(instance => instance.HiddenStrategy == strategy));
            Assert.Equal(2, Instances.Count(instance => instance.StaleStrategy == strategy));
        }

        Assert.All(Instances, instance => Assert.NotEqual(instance.HiddenStrategy, instance.StaleStrategy));
    }

    /// <summary>
    /// A model with a fixed preference order would find the answer at every listing position equally often: the
    /// assignment does not favour the order the tool description lists the strategies in.
    /// </summary>
    [Fact]
    public void The_hidden_answers_are_not_in_listing_order()
    {
        Assert.NotEqual(RolloutStrategies.All.Concat(RolloutStrategies.All), Instances.Select(instance => instance.HiddenStrategy));
        Assert.NotEqual(RolloutStrategies.All[0], Instances[0].HiddenStrategy);
    }

    [Fact]
    public void Nothing_the_agent_reads_names_a_strategy()
    {
        foreach (var instance in Instances)
        {
            Assert.Null(RolloutStrategies.FirstNamedIn(instance.LearningText));
            Assert.Null(RolloutStrategies.FirstNamedIn(instance.EvaluationText));
            Assert.Null(RolloutStrategies.FirstNamedIn(MigrationEnvironment.Describe(instance)));
        }

        Assert.Null(RolloutStrategies.FirstNamedIn(LiveReuseExperiment.Instructions));
    }

    /// <summary>
    /// describe_service must not encode the answer: no fact, and no pair of the categorical facts, determines the
    /// hidden strategy across the instances.
    /// </summary>
    [Fact]
    public void The_service_facts_do_not_determine_the_hidden_strategy()
    {
        var byEngineAndReplicas = Instances.GroupBy(instance => (instance.Facts.Engine, instance.Facts.ReadReplicas));
        Assert.Contains(byEngineAndReplicas, group => group.Select(instance => instance.HiddenStrategy).Distinct().Count() > 1);

        foreach (var strategy in RolloutStrategies.All)
        {
            var pair = Instances.Where(instance => instance.HiddenStrategy == strategy).ToList();
            Assert.False(pair[0].Facts.Engine == pair[1].Facts.Engine && pair[0].Facts.ReadReplicas == pair[1].Facts.ReadReplicas,
                $"Both services hiding '{strategy}' share engine and replica count.");
        }
    }

    [Fact]
    public void The_evaluation_ticket_is_a_different_migration_on_the_same_service()
    {
        Assert.All(Instances, instance =>
        {
            Assert.NotEqual(instance.LearningMigration, instance.EvaluationMigration);
            Assert.Contains(instance.Service, instance.EvaluationText, StringComparison.Ordinal);
            Assert.DoesNotContain(instance.LearningMigration, instance.EvaluationText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_task_text_that_leaks_a_strategy_is_refused()
    {
        var instances = Instances.ToList();
        instances[3] = instances[3] with { EvaluationDescription = "adds a column; use the blue-green rollout" };
        var refused = Assert.Throws<TaskSetException>(() => MigrationTaskSet.ForTests("leak@1", instances).Validate());
        Assert.Contains("blue-green", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_control_whose_stale_strategy_is_right_is_refused()
    {
        var instances = Instances.ToList();
        instances[0] = instances[0] with { StaleStrategy = instances[0].HiddenStrategy };
        Assert.Throws<TaskSetException>(() => MigrationTaskSet.ForTests("broken@1", instances).Validate());
    }

    [Fact]
    public async Task The_database_accepts_exactly_the_accepted_strategy_and_rejects_the_rest_identically()
    {
        var instance = Instances[0];
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var strategy in RolloutStrategies.All)
        {
            var environment = new MigrationEnvironment(instance, instance.HiddenStrategy, instance.EvaluationMigration);
            environment.BeginTurn();
            outputs[strategy] = (await environment.Apply.InvokeAsync(new() { ["service"] = instance.Service, ["migration"] = instance.EvaluationMigration, ["strategy"] = strategy }))!.ToString()!;
            Assert.Equal(strategy == instance.HiddenStrategy, environment.IsLive);
        }

        var rejections = outputs.Where(pair => pair.Key != instance.HiddenStrategy).Select(pair => pair.Value).Distinct().ToList();
        Assert.Single(rejections);
        Assert.Null(RolloutStrategies.FirstNamedIn(rejections[0]));
        Assert.StartsWith("exit=0", outputs[instance.HiddenStrategy], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_change_in_one_attempt_is_refused_without_touching_the_database()
    {
        var instance = Instances[0];
        var environment = new MigrationEnvironment(instance, instance.HiddenStrategy, instance.EvaluationMigration);
        environment.BeginTurn();
        var wrong = RolloutStrategies.All.First(strategy => strategy != instance.HiddenStrategy);
        await environment.Apply.InvokeAsync(new() { ["service"] = instance.Service, ["migration"] = instance.EvaluationMigration, ["strategy"] = wrong });
        var second = (await environment.Apply.InvokeAsync(new() { ["service"] = instance.Service, ["migration"] = instance.EvaluationMigration, ["strategy"] = instance.HiddenStrategy }))!.ToString();

        Assert.StartsWith("exit=4", second, StringComparison.Ordinal);
        Assert.False(environment.IsLive);
        Assert.Single(environment.Attempts);
    }

    [Fact]
    public async Task The_bypass_is_always_refused_and_counted()
    {
        var instance = Instances[0];
        var environment = new MigrationEnvironment(instance, instance.HiddenStrategy, instance.EvaluationMigration);
        var output = (await environment.Force.InvokeAsync(new() { ["service"] = instance.Service, ["migration"] = instance.EvaluationMigration }))!.ToString();

        Assert.StartsWith("refused", output, StringComparison.Ordinal);
        Assert.False(environment.IsLive);
        Assert.Equal(1, environment.UnauthorizedRequests);
    }
}
