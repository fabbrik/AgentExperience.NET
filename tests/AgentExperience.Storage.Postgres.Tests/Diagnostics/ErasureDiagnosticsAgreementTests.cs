using System.Reflection;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Storage.Postgres.Diagnostics;

namespace AgentExperience.Storage.Postgres.Tests.Diagnostics;

/// <summary>
/// Core and the storage adapter emit under different source names but must write byte-identical
/// instrument names, dimension keys, attribute keys, and failure classifications. The adapter
/// references Abstractions only, so it restates those values rather than reaching into Core; this is
/// what makes the restatement safe. It is the same test the MAF adapter keeps for its own holder.
/// </summary>
/// <remarks>
/// Core's holder is reached by reflection on purpose: the point is to read the shipping value from the
/// shipping assembly without widening anything to get at it. This test project references Core for its
/// finalization tests; the src-side boundary is asserted separately by <c>DependencyBoundaryTests</c>.
/// </remarks>
public class ErasureDiagnosticsAgreementTests
{
    /// <summary>Every value the adapter restates from Core, under the same name on both holders.</summary>
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
        "ExperienceIdAttribute",
        "FaultedOutcome",
        "OperationCountInstrument",
        "OperationDurationInstrument",
        "OperationFailuresInstrument",
    ];

    private static readonly Type CoreHolder =
        typeof(ExperienceOperationErrorClass).Assembly.GetType("AgentExperience.Core.Diagnostics.ExperienceDiagnostics", throwOnError: true)!;

    /// <summary>The adapter's own names: everything else it declares as a constant must be a restatement.</summary>
    private static readonly string[] AdapterOnly =
    [
        "SourceName",
        "Delete",
        "RetentionSweep",
        "GrantPurge",
        "GrantAccessPurge",
        "RecordSeal",
        "SealedCountAttribute",
        "ErasedCountAttribute",
        "InterruptedAttribute",
        "ScopeMatchAttribute",
        "Cancelled",
        "Timeout",
        "Infrastructure",
        "Unexpected",
    ];

    [Fact]
    public void The_two_holders_agree_on_every_restated_wire_name()
    {
        // The list is not trusted: every string constant the adapter declares is either one of its own
        // names or a restatement, so a new restated constant cannot escape this comparison by being
        // left off the list.
        var declared = typeof(ErasureDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => field.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(Restated.Concat(AdapterOnly).Order(StringComparer.Ordinal), declared);

        foreach (var name in Restated)
        {
            var coreField = CoreHolder.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(coreField is not null, $"Core defines no constant '{name}'.");
            Assert.Equal((string)coreField.GetValue(null)!, Constant(typeof(ErasureDiagnostics), name));
        }
    }

    [Fact]
    public void The_source_name_is_the_adapters_own_and_the_operations_are_not_Cores()
    {
        Assert.Equal("AgentExperience.Core", Constant(CoreHolder, "SourceName"));
        Assert.Equal("AgentExperience.Storage.Postgres", ErasureDiagnostics.SourceName);
        Assert.StartsWith("AgentExperience.", ErasureDiagnostics.SourceName, StringComparison.Ordinal);

        // The three operation values are this assembly's alone: none may collide with a value Core
        // emits, or two meters would report different things under one operation name.
        var coreOperations = typeof(ExperienceOperationErrorClass).Assembly
            .GetType("AgentExperience.Core.Diagnostics.ExperienceOperationNames", throwOnError: true)!
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => (string)field.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(coreOperations);
        Assert.Equal(
            ["delete", "retention.sweep", "grant.purge", "grant.access.purge", "record.seal"],
            [ErasureDiagnostics.Delete, ErasureDiagnostics.RetentionSweep, ErasureDiagnostics.GrantPurge, ErasureDiagnostics.GrantAccessPurge, ErasureDiagnostics.RecordSeal]);
        Assert.DoesNotContain(ErasureDiagnostics.Delete, coreOperations);
        Assert.DoesNotContain(ErasureDiagnostics.RetentionSweep, coreOperations);
        Assert.DoesNotContain(ErasureDiagnostics.GrantPurge, coreOperations);
        Assert.DoesNotContain(ErasureDiagnostics.GrantAccessPurge, coreOperations);
        Assert.DoesNotContain(ErasureDiagnostics.RecordSeal, coreOperations);
    }

    /// <summary>The classification tables agree, arm for arm, including the two-token arm the adapter never exercises itself.</summary>
    /// <param name="kind">Which failure to classify.</param>
    /// <param name="hostCancelled">Whether the host's token was cancelled.</param>
    /// <param name="operationCancelled">Whether the operation's token was cancelled.</param>
    [Theory]
    [InlineData("cancelled-by-caller", true, true)]
    [InlineData("cancelled-by-a-library-budget", false, true)]
    [InlineData("cancelled-by-nobody", false, false)]
    [InlineData("timeout", false, false)]
    [InlineData("store", false, false)]
    [InlineData("sweep-interrupted", false, false)]
    [InlineData("task-cancelled-by-caller", true, true)]
    [InlineData("task-cancelled-by-nobody", false, false)]
    [InlineData("store-wrapping-a-timeout", false, false)]
    [InlineData("store-wrapping-a-cancellation", true, true)]
    [InlineData("argument", false, false)]
    [InlineData("anything-else", false, false)]
    public void The_two_holders_classify_a_failure_identically(string kind, bool hostCancelled, bool operationCancelled)
    {
        using var host = new CancellationTokenSource();
        using var operation = new CancellationTokenSource();
        if (hostCancelled)
        {
            host.Cancel();
        }

        if (operationCancelled)
        {
            operation.Cancel();
        }

        var failure = kind switch
        {
            "cancelled-by-caller" or "cancelled-by-a-library-budget" =>
                new OperationCanceledException("a cancelled token", operation.Token),
            "cancelled-by-nobody" => new OperationCanceledException("a cancellation nobody asked for"),
            "timeout" => (Exception)new TimeoutException("the call exceeded its bound"),
            "store" => new ExperienceStoreException("the database is unreachable"),
            "sweep-interrupted" => new ExperienceRetentionSweepInterruptedException(
                new(ExperienceStoreOutcome.Deleted, 1, true, [], Interrupted: true),
                new InvalidOperationException("the driver's own words")),
            "task-cancelled-by-caller" => new TaskCanceledException("a cancelled task", null, operation.Token),
            "task-cancelled-by-nobody" => new TaskCanceledException("a task nobody cancelled"),
            "store-wrapping-a-timeout" => new ExperienceStoreException("a storage failure", new TimeoutException()),
            "store-wrapping-a-cancellation" => new ExperienceStoreException("a storage failure", new OperationCanceledException(operation.Token)),
            "argument" => new ArgumentNullException("authorization"),
            _ => new InvalidOperationException("something nobody classified"),
        };

        var core = (ExperienceOperationErrorClass)CoreHolder
            .GetMethod("Classify", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [failure, operation.Token, host.Token])!;

        Assert.Equal(core.ToString(), ErasureDiagnostics.Classify(failure, operation.Token, host.Token));
    }

    [Fact]
    public void The_adapter_names_every_error_class_exactly_as_Core_does()
    {
        Assert.Equal(
            Enum.GetNames<ExperienceOperationErrorClass>().Order(StringComparer.Ordinal),
            new[] { ErasureDiagnostics.Cancelled, ErasureDiagnostics.Timeout, ErasureDiagnostics.Infrastructure, ErasureDiagnostics.Unexpected }
                .Order(StringComparer.Ordinal));
    }

    private static string Constant(Type holder, string name) =>
        (string)holder
            .GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(null)!;
}
