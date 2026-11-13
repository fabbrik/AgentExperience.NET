using AgentExperience.Core.ReflectiveExecution;
using AgentExperience.Sample.Reflective;

namespace AgentExperience.Sample.QuickStart.Tests.Reflective;

public class ReflectiveDemoTests
{
    [Fact]
    public async Task Sample_A_corrects_a_failed_first_attempt_and_the_host_check_confirms_it()
    {
        using var model = new ReflectiveDemo.ScriptedModel();
        var runs = await ReflectiveDemo.RunAsync(model, TextWriter.Null);

        var first = runs[0];
        Assert.Equal(["bump-version", "pin-version"], first.FixesTried);
        Assert.False(first.LessonInjected);
        Assert.Equal(2, first.Reflection!.Attempts);
        Assert.Equal(ReflectionStopReason.VerifiedSuccess, first.Reflection.StopReason);
        Assert.True(first.Reflection.RetryReachedVerifiedSuccess);
        Assert.True(first.BuildGreen);
    }

    [Fact]
    public async Task Sample_B_second_task_is_handed_the_lesson_and_needs_no_reflection()
    {
        using var model = new ReflectiveDemo.ScriptedModel();
        var runs = await ReflectiveDemo.RunAsync(model, TextWriter.Null);

        var second = runs[1];
        Assert.True(second.LessonInjected);
        Assert.Equal(["pin-version"], second.FixesTried);
        Assert.Equal(1, second.Reflection!.Attempts);
        Assert.False(second.Reflection.RetryHappened);
        Assert.True(second.BuildGreen);
    }

    [Fact]
    public async Task The_output_is_deterministic()
    {
        using var a = new ReflectiveDemo.ScriptedModel();
        using var b = new ReflectiveDemo.ScriptedModel();
        var one = new StringWriter();
        var two = new StringWriter();
        await ReflectiveDemo.RunAsync(a, one);
        await ReflectiveDemo.RunAsync(b, two);
        Assert.Equal(one.ToString(), two.ToString());
    }
}

public class ReflectiveArmsTests
{
    [Fact]
    public async Task The_four_arms_show_the_mechanics_and_claim_nothing_about_a_real_model()
    {
        var arms = await ReflectiveDemo.CompareArmsAsync(TextWriter.Null);
        var byName = arms.ToDictionary(a => a.Arm);

        // The scripted model obeys what it is handed, so: no help fails, reflection and memory each succeed, and memory
        // spends fewer calls than reflection because it needs no failed first attempt.
        Assert.Equal(0, byName["baseline"].Verified);
        Assert.Equal(3, byName["reflection only"].Verified);
        Assert.Equal(3, byName["memory only"].Verified);
        Assert.Equal(3, byName["reflection + memory"].Verified);
        Assert.True(byName["memory only"].ModelCalls < byName["reflection only"].ModelCalls);
    }
}
