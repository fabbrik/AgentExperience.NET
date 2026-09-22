using System.Reflection;
using AgentExperience.Core.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>
/// Core and the adapter emit under different source names but must write byte-identical instrument
/// names, dimension keys, attribute keys, and failure classifications. Core's holder is internal to
/// Core and stays that way, so the adapter restates those values rather than being handed access to
/// every Core internal for the sake of a dozen strings. This is what makes that restatement safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grant would not have prevented the drift anyway.</b> The shared values are
/// <see langword="const"/>, so they are baked into the referencing assembly at compile time; Core and
/// the adapter ship as independent packages and can be restored at different versions, which means a
/// value changed in one and not the other diverges either way. What an <c>InternalsVisibleTo</c> buys
/// is the appearance of a single definition; what this test buys is the divergence showing up as a
/// failing build.
/// </para>
/// <para>
/// Core's holder is reached by reflection, deliberately: the point is to read the shipping value,
/// from the shipping assembly, without widening anything to get at it.
/// </para>
/// </remarks>
public class DiagnosticsAgreementTests
{
    /// <summary>
    /// Every value the adapter restates from Core. The names are identical on both holders, which is
    /// also the convention that keeps a new restated constant from being missed here.
    /// </summary>
    private static readonly string[] Restated =
    [
        "SpanNamePrefix",
        "OperationDimension",
        "OutcomeDimension",
        "ErrorClassDimension",
        "NestedDimension",
        "OperationAttribute",
        "OutcomeAttribute",
        "ErrorClassAttribute",
        "ErrorTypeAttribute",
        "CorrelationIdAttribute",
        "FaultedOutcome",
        "OperationCountInstrument",
        "OperationDurationInstrument",
        "OperationFailuresInstrument",
    ];

    private static readonly Type CoreHolder =
        typeof(ExperienceOperationErrorClass).Assembly.GetType("AgentExperience.Core.Diagnostics.ExperienceDiagnostics", throwOnError: true)!;

    private static readonly Type AdapterHolder = typeof(InjectionDiagnostics);

    [Fact]
    public void The_two_holders_agree_on_every_restated_wire_name()
    {
        // The sanity guard: a typo in the list above, or a Core rename, must not make this pass by
        // comparing nothing.
        Assert.Equal(14, Restated.Length);

        foreach (var name in Restated)
        {
            var core = Constant(CoreHolder, name);
            var adapter = Constant(AdapterHolder, name);

            Assert.False(string.IsNullOrWhiteSpace(core), $"Core defines no constant '{name}'.");
            Assert.Equal(core, adapter);
        }
    }

    [Fact]
    public void The_two_holders_deliberately_disagree_on_the_source_name()
    {
        // Assembly-scoped names are the one thing that must differ: a text-only host subscribes to
        // Core without pulling the adapter into its telemetry configuration, and AgentExperience.*
        // takes both for a host that wants them together.
        Assert.Equal("AgentExperience.Core", Constant(CoreHolder, "SourceName"));
        Assert.Equal("AgentExperience.MicrosoftAgentFramework", InjectionDiagnostics.SourceName);
        Assert.StartsWith(Constant(CoreHolder, "SpanNamePrefix"), "agentexperience.inject", StringComparison.Ordinal);
    }

    /// <summary>
    /// The classification tables agree, arm for arm. Replacing the adapter's whole switch with
    /// <c>=&gt; Cancelled</c> used to leave every adapter test passing.
    /// </summary>
    /// <param name="kind">Which failure to classify.</param>
    /// <param name="hostCancelled">Whether the token the host handed the outermost operation was cancelled.</param>
    /// <param name="operationCancelled">Whether the token the operation itself was handed was cancelled.</param>
    [Theory]
    [InlineData("cancelled-by-caller", true, true)]
    [InlineData("cancelled-by-a-library-budget", false, true)]
    [InlineData("cancelled-by-nobody", false, false)]
    [InlineData("timeout", false, false)]
    [InlineData("store", false, false)]
    [InlineData("anything-else", false, false)]
    public void The_two_holders_classify_a_failure_identically(string kind, bool hostCancelled, bool operationCancelled)
    {
        // Both tokens, so the arm that separates a deadline this library imposed on an inner step from
        // the caller having given up is compared too. The adapter passes the same token twice in
        // production -- it imposes no budget of its own -- but its table must still be Core's.
        using var host = new CancellationTokenSource();
        using var operationCancellation = new CancellationTokenSource();
        if (hostCancelled)
        {
            host.Cancel();
        }

        if (operationCancelled)
        {
            operationCancellation.Cancel();
        }

        var failure = kind switch
        {
            "cancelled-by-caller" or "cancelled-by-a-library-budget" =>
                new OperationCanceledException("a cancelled token", operationCancellation.Token),
            "cancelled-by-nobody" => new OperationCanceledException("a cancellation nobody asked for"),
            "timeout" => (Exception)new TimeoutException("the call exceeded its bound"),
            "store" => new ExperienceStoreException("the database is unreachable"),
            _ => new InvalidOperationException("something nobody classified"),
        };

        var core = Classify(failure, operationCancellation.Token, host.Token);
        var adapter = InjectionDiagnostics.Classify(failure, operationCancellation.Token, host.Token);

        Assert.Equal(core, adapter);
        Assert.Equal(Name(core), InjectionDiagnostics.Name(adapter));
    }

    [Fact]
    public void The_two_holders_name_every_error_class_identically()
    {
        foreach (var errorClass in Enum.GetValues<ExperienceOperationErrorClass>())
        {
            Assert.Equal(errorClass.ToString(), InjectionDiagnostics.Name(errorClass));
            Assert.Equal(Name(errorClass), InjectionDiagnostics.Name(errorClass));
        }
    }

    private static string Constant(Type holder, string name) =>
        (string)holder
            .GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(null)!;

    private static ExperienceOperationErrorClass Classify(
        Exception exception,
        CancellationToken operationToken,
        CancellationToken hostToken) =>
        (ExperienceOperationErrorClass)CoreHolder
            .GetMethod("Classify", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [exception, operationToken, hostToken])!;

    private static string Name(ExperienceOperationErrorClass errorClass) =>
        (string)CoreHolder
            .GetMethod("Name", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [errorClass])!;
}
