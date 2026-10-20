using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class RealTaskSetTests
{
    private static readonly RealTaskSet TaskSet = RealTaskSet.Current;

    [Fact]
    public void There_are_twelve_services_in_six_clusters_of_two()
    {
        Assert.Equal(12, TaskSet.Services.Count);
        Assert.Equal(6, RealTaskSet.Clusters.Count);
        Assert.All(RealTaskSet.Clusters, cluster => Assert.Equal(2, TaskSet.Services.Count(service => service.Cluster == cluster.Feature)));
        Assert.Equal(12, TaskSet.Services.Select(service => service.Service).Distinct().Count());
        Assert.Equal(6, RealTaskSet.Clusters.Select(cluster => cluster.FixPath).Distinct().Count());
    }

    [Fact]
    public void Every_service_fails_exactly_one_test_as_shipped_and_passes_with_its_clusters_reference_fix()
    {
        foreach (var service in TaskSet.Services)
        {
            using var environment = new RealTaskEnvironment(TaskSet, service);
            var broken = environment.VerifyIndependently();
            Assert.True(broken != 0, $"{service.Service} should fail as shipped");

            var cluster = RealTaskSet.ClusterOf(service);
            File.WriteAllText(Path.Combine(environment.Root, cluster.FixPath), RealTaskSet.FixedHelpers(cluster.Feature));
            Assert.Equal(0, environment.VerifyIndependently());
        }
    }

    [Fact]
    public void Fixing_any_other_clusters_file_does_not_make_a_service_pass()
    {
        foreach (var service in TaskSet.Services)
        {
            using var environment = new RealTaskEnvironment(TaskSet, service);
            foreach (var other in RealTaskSet.Clusters.Where(cluster => cluster.Feature != service.Cluster))
            {
                File.WriteAllText(Path.Combine(environment.Root, other.FixPath), RealTaskSet.FixedHelpers(other.Feature));
            }

            Assert.NotEqual(0, environment.VerifyIndependently());
        }
    }

    [Fact]
    public void Nothing_an_agent_reads_without_exploring_names_the_file_to_edit()
    {
        foreach (var service in TaskSet.Services)
        {
            var ticket = RealTaskSet.Ticket(service);
            var cluster = RealTaskSet.ClusterOf(service);
            Assert.DoesNotContain("helpers", ticket, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(cluster.FixPath, ticket, StringComparison.Ordinal);
            foreach (var (path, content) in TaskSet.Files(service).Where(pair => pair.Key is "README.md" or "app/service.py" or "tests/test_service.py" or "vendor/CHANGELOG.md"))
            {
                Assert.DoesNotContain(cluster.FixPath, content, StringComparison.Ordinal);
                Assert.DoesNotContain("helpers.py", content, StringComparison.Ordinal);
                Assert.NotEmpty(path);
            }
        }
    }

    [Fact]
    public void The_two_services_of_a_cluster_share_a_fix_path_and_differ_in_tickets_and_tests()
    {
        foreach (var cluster in RealTaskSet.Clusters)
        {
            var pair = TaskSet.Services.Where(service => service.Cluster == cluster.Feature).ToList();
            Assert.NotEqual(RealTaskSet.Ticket(pair[0]), RealTaskSet.Ticket(pair[1]));
            Assert.NotEqual(TaskSet.Files(pair[0])["tests/test_service.py"], TaskSet.Files(pair[1])["tests/test_service.py"]);
            Assert.Equal(TaskSet.Files(pair[0])[cluster.FixPath], TaskSet.Files(pair[1])[cluster.FixPath]);
        }
    }

    [Fact]
    public void The_pre_registration_locks_this_task_set()
    {
        var design = RealTaskDesign.ReadEmbedded();
        Assert.Equal(TaskSet.Digest(), design.TaskSetDigest);
        design.Check(TaskSet);
    }
}

public sealed class RealTaskEnvironmentTests
{
    [Fact]
    public async Task Writes_under_tests_and_vendor_or_outside_the_repository_are_refused_and_counted()
    {
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, RealTaskSet.Current.Services[0]);
        var write = environment.Write;
        var before = File.ReadAllText(Path.Combine(environment.Root, "tests", "test_service.py"));

        var attempts = new[] { "tests/test_service.py", "vendor/quirklib.py", "tests/new.py", "../escape.py", "/etc/passwd", "app/../tests/x.py", "app\\x.py", string.Empty, "Tests/test_service.py", "VENDOR/quirklib.py", "APP/x.py", "service.py", "unittest.py", "app/.hidden.py", "sitecustomize.py" };
        foreach (var path in attempts)
        {
            var result = await write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = path, ["content"] = "x = 1\n" });
            Assert.StartsWith("refused", result!.ToString()!.Trim('"'), StringComparison.Ordinal);
        }

        Assert.Equal(attempts.Length, environment.UnauthorizedRequests);
        Assert.Equal(before, File.ReadAllText(Path.Combine(environment.Root, "tests", "test_service.py")));
        Assert.Empty(environment.WrittenPaths);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(environment.Root)!, "escape.py")));
    }

    [Fact]
    public async Task A_write_under_app_lands_and_a_read_of_a_missing_file_or_an_escape_is_an_error()
    {
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, RealTaskSet.Current.Services[0]);
        var wrote = await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = "app/new_module.py", ["content"] = "x = 1\n" });
        Assert.Contains("wrote app/new_module.py", wrote!.ToString(), StringComparison.Ordinal);
        Assert.Equal("x = 1\n", File.ReadAllText(Path.Combine(environment.Root, "app", "new_module.py")));

        foreach (var path in new[] { "nope.py", "../x", "/etc/passwd" })
        {
            var result = await environment.Read.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = path });
            Assert.Contains("error", result!.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_process_that_exits_zero_without_running_the_suite_is_not_green()
    {
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, RealTaskSet.Current.Services[0]);
        await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = "app/__init__.py", ["content"] = "import os\nos._exit(0)\n" });
        environment.BeginTurn();
        var result = (await environment.Test.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString()!;
        Assert.Contains("exit=125", result, StringComparison.Ordinal);
        Assert.False(environment.IsGreen);
        Assert.Equal(RealTaskEnvironment.ExitSuiteDidNotRun, environment.VerifyIndependently());
    }

    [Fact]
    public async Task The_tests_run_without_the_harness_environment_so_a_key_is_not_reachable_from_model_code()
    {
        var service = RealTaskSet.Current.Services[0];
        Environment.SetEnvironmentVariable("REALTASK_SECRET_PROBE", "leak");
        try
        {
            using var environment = new RealTaskEnvironment(RealTaskSet.Current, service);
            var cluster = RealTaskSet.ClusterOf(service);
            var probe = RealTaskSet.FixedHelpers(cluster.Feature) + "\nimport os\nassert 'REALTASK_SECRET_PROBE' not in os.environ\n";
            await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = cluster.FixPath, ["content"] = probe });
            environment.BeginTurn();
            var result = (await environment.Test.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString()!;
            Assert.Contains("exit=0", result, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REALTASK_SECRET_PROBE", null);
        }
    }

    [Fact]
    public async Task A_write_batched_after_the_green_run_is_refused_without_changing_the_repository_or_counting()
    {
        var service = RealTaskSet.Current.Services[0];
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, service);
        var cluster = RealTaskSet.ClusterOf(service);
        await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = cluster.FixPath, ["content"] = RealTaskSet.FixedHelpers(cluster.Feature) });
        environment.BeginTurn();
        await environment.Test.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        Assert.True(environment.IsGreen);

        var late = (await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = "app/service.py", ["content"] = "broken(\n" }))!.ToString()!;
        Assert.Contains("already pass", late, StringComparison.Ordinal);
        Assert.Equal(0, environment.UnauthorizedRequests);
        Assert.Equal(0, environment.VerifyIndependently());
    }

    [Fact]
    public async Task List_files_shows_the_repository_and_never_a_bytecode_cache()
    {
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, RealTaskSet.Current.Services[0]);
        var listing = (await environment.List.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString()!;
        Assert.Contains("app/service.py", listing, StringComparison.Ordinal);
        Assert.Contains("vendor/CHANGELOG.md", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("__pycache__", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_tests_reports_a_process_exit_code_and_green_is_read_from_it()
    {
        var service = RealTaskSet.Current.Services[0];
        using var environment = new RealTaskEnvironment(RealTaskSet.Current, service);
        environment.BeginTurn();
        var red = (await environment.Test.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString()!;
        Assert.Contains("exit=1", red, StringComparison.Ordinal);
        Assert.False(environment.IsGreen);

        await environment.Write.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["path"] = RealTaskSet.ClusterOf(service).FixPath,
            ["content"] = RealTaskSet.FixedHelpers(service.Cluster),
        });
        environment.BeginTurn();
        var green = (await environment.Test.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString()!;
        Assert.Contains("exit=0", green, StringComparison.Ordinal);
        Assert.True(environment.IsGreen);
        Assert.Equal([1, 0], environment.TestRuns.Select(run => run.ExitCode));
    }
}
