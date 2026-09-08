using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Compliance.Redaction;

namespace AgentExperience.CompatibilityProof;

/// <summary>
/// Story 1.7, Track 4 (AC4): proves a purely deterministic check composes with
/// <c>Microsoft.Extensions.AI.Evaluation</c> 10.9.0 as a custom <see cref="IEvaluator"/> returning
/// <see cref="BooleanMetric"/>, and that a custom <see cref="Redactor"/> subclass over
/// <c>Microsoft.Extensions.Compliance.Redaction</c> 10.9.0 only ever performs a flat per-value transform --
/// nested-payload traversal, field classification, and rejection of unknown fields all stay
/// AgentExperience.NET's own code, never the library's.
/// </summary>
/// <remarks>
/// Grounding: <c>story-1-7-research-digest.md</c>, Track 4.
/// Sources: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.evaluation.ievaluator ,
/// https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries ,
/// https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction ,
/// https://github.com/dotnet/extensions/discussions/4735 (confirms <see cref="Redactor"/> has no object-graph
/// awareness and no built-in "reject" outcome -- only "redact in place").
/// </remarks>
public class EvaluationRedactionProof
{
    #region Track 4a: deterministic check as a custom IEvaluator / BooleanMetric

    /// <summary>Contextual input for <see cref="DeterministicExitCodeEvaluator"/>: an exit-code-style outcome.</summary>
    private sealed class ExitCodeEvaluationContext(int exitCode)
        : EvaluationContext("ExitCode", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        public int ExitCode { get; } = exitCode;
    }

    /// <summary>
    /// Wraps a purely deterministic, non-LLM check (an exit-code-style bool, per the digest's example) as a
    /// custom <see cref="IEvaluator"/>. No model call, no <see cref="ChatConfiguration"/> is used -- proving the
    /// interface is genuinely general-purpose, not LLM-bound.
    /// </summary>
    private sealed class DeterministicExitCodeEvaluator : IEvaluator
    {
        public const string MetricName = "ToolExitCodeZero";

        public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [MetricName];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            var exitCodeContext = additionalContext?.OfType<ExitCodeEvaluationContext>().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"{nameof(DeterministicExitCodeEvaluator)} requires an {nameof(ExitCodeEvaluationContext)} in additionalContext.");

            var passed = exitCodeContext.ExitCode == 0;
            var metric = new BooleanMetric(MetricName, passed, reason: $"tool exit code was {exitCodeContext.ExitCode}");

            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }
    }

    private static readonly ChatMessage[] NoMessages = [];
    private static readonly ChatResponse PlaceholderResponse = new(new ChatMessage(ChatRole.Assistant, "n/a"));

    [Fact]
    public async Task Deterministic_evaluator_reports_a_passing_BooleanMetric_for_exit_code_zero()
    {
        var evaluator = new DeterministicExitCodeEvaluator();

        var result = await evaluator.EvaluateAsync(
            NoMessages, PlaceholderResponse, additionalContext: [new ExitCodeEvaluationContext(0)]);

        var metric = result.Get<BooleanMetric>(DeterministicExitCodeEvaluator.MetricName);
        Assert.True(metric.Value);
    }

    [Fact]
    public async Task Deterministic_evaluator_reports_a_failing_BooleanMetric_for_a_nonzero_exit_code()
    {
        var evaluator = new DeterministicExitCodeEvaluator();

        var result = await evaluator.EvaluateAsync(
            NoMessages, PlaceholderResponse, additionalContext: [new ExitCodeEvaluationContext(1)]);

        var metric = result.Get<BooleanMetric>(DeterministicExitCodeEvaluator.MetricName);
        Assert.False(metric.Value);
        Assert.Contains("1", metric.Reason);
    }

    #endregion

    #region Track 4b: custom Redactor -- flat transform only; traversal/rejection stay ours

    /// <summary>
    /// A minimal custom <see cref="Redactor"/>: replaces any non-empty input with a fixed mask. This is the
    /// entire library-owned surface -- a single-value, flat string transform. Everything else in this file's
    /// second half (walking a nested payload, deciding which fields are secret, and rejecting unknown fields)
    /// is AgentExperience.NET's own code, calling this class only at each classified leaf.
    /// </summary>
    private sealed class FixedMaskRedactor : Redactor
    {
        private const string Mask = "***REDACTED***";

        public override int GetRedactedLength(ReadOnlySpan<char> input) => input.IsEmpty ? 0 : Mask.Length;

        public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
        {
            if (source.IsEmpty)
            {
                return 0;
            }

            Mask.AsSpan().CopyTo(destination);
            return Mask.Length;
        }
    }

    /// <summary>Stand-in for AgentExperience.NET's payload allowlist/classification policy (not part of the library).</summary>
    private static readonly HashSet<string> AllowedFieldNames = new(StringComparer.Ordinal) { "tool", "to", "note", "nested" };

    /// <summary>Stand-in for AgentExperience.NET's secret-field classification (not part of the library).</summary>
    private static readonly HashSet<string> SecretFieldNames = new(StringComparer.Ordinal) { "apiKey", "authToken" };

    private sealed record SanitizeOutcome(Dictionary<string, object?> SanitizedFields, List<string> RejectedFields);

    /// <summary>
    /// AgentExperience.NET's own recursive object-graph traversal, demonstrated here only over the
    /// dictionary-of-dictionaries shape this file's tests actually exercise -- <em>not</em> a claim of
    /// arbitrary-payload coverage. At each field: a classified-secret leaf value is redacted via the library's
    /// flat <see cref="Redactor.Redact(string?)"/>; an allowlisted field (including a nested dictionary) passes
    /// through/recurses; anything else is rejected (omitted from the sanitized output) -- a policy stage the
    /// <see cref="Redactor"/> API has no equivalent for, since it only ever redacts a value in place and never
    /// reports "reject this field".
    /// </summary>
    /// <remarks>
    /// Two shapes this proof does not handle, left as open design questions for Story 1.4's real
    /// implementation rather than bugs to fix in this proof: (a) a secret-classified key whose own value is a
    /// nested dictionary falls through to unconditional container-recursion ahead of the secret check below, so
    /// an allowlisted-named sub-field nested under a secret-classified key would not be masked as a unit; (b) an
    /// allowlisted field holding a list/array of nested dictionaries is never recursed into at all (only
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> values are), so a secret nested inside a list passes
    /// through unredacted.
    /// </remarks>
    private static SanitizeOutcome SanitizePayload(IReadOnlyDictionary<string, object?> payload, Redactor redactor)
    {
        var sanitized = new Dictionary<string, object?>();
        var rejected = new List<string>();

        foreach (var (key, value) in payload)
        {
            if (SecretFieldNames.Contains(key) && value is string secretText)
            {
                sanitized[key] = redactor.Redact(secretText);
            }
            else if (value is IReadOnlyDictionary<string, object?> nested)
            {
                var nestedOutcome = SanitizePayload(nested, redactor);
                sanitized[key] = nestedOutcome.SanitizedFields;
                rejected.AddRange(nestedOutcome.RejectedFields.Select(f => $"{key}.{f}"));
            }
            else if (AllowedFieldNames.Contains(key))
            {
                sanitized[key] = value;
            }
            else
            {
                rejected.Add(key);
            }
        }

        return new SanitizeOutcome(sanitized, rejected);
    }

    [Fact]
    public void Custom_traversal_redacts_classified_leaf_values_at_any_depth_via_the_library_Redactor()
    {
        var redactor = new FixedMaskRedactor();
        var payload = new Dictionary<string, object?>
        {
            ["tool"] = "send_email",
            ["to"] = "user@example.com",
            ["apiKey"] = "sk-live-super-secret",
            ["nested"] = new Dictionary<string, object?>
            {
                ["authToken"] = "another-secret-value",
                ["note"] = "hello world",
            },
        };

        var outcome = SanitizePayload(payload, redactor);

        Assert.Equal("***REDACTED***", outcome.SanitizedFields["apiKey"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(outcome.SanitizedFields["nested"]);
        Assert.Equal("***REDACTED***", nested["authToken"]);

        // Allowlisted plain fields pass through unredacted, at any depth.
        Assert.Equal("user@example.com", outcome.SanitizedFields["to"]);
        Assert.Equal("hello world", nested["note"]);
        Assert.Empty(outcome.RejectedFields);
    }

    [Fact]
    public void Custom_traversal_rejects_unknown_fields_at_any_depth_a_policy_the_library_has_no_equivalent_for()
    {
        var redactor = new FixedMaskRedactor();
        var payload = new Dictionary<string, object?>
        {
            ["tool"] = "send_email",
            ["topLevelUnknown"] = "should be rejected",
            ["nested"] = new Dictionary<string, object?>
            {
                ["note"] = "hello world",
                ["unexpectedField"] = "should be rejected too",
            },
        };

        var outcome = SanitizePayload(payload, redactor);

        Assert.DoesNotContain("topLevelUnknown", outcome.SanitizedFields.Keys);
        Assert.Contains("topLevelUnknown", outcome.RejectedFields);

        var nested = Assert.IsType<Dictionary<string, object?>>(outcome.SanitizedFields["nested"]);
        Assert.DoesNotContain("unexpectedField", nested.Keys);
        Assert.Contains("nested.unexpectedField", outcome.RejectedFields);
    }

    [Fact]
    public void FixedMaskRedactor_never_reveals_any_part_of_the_original_secret_value()
    {
        var redactor = new FixedMaskRedactor();

        var redacted = redactor.Redact("sk-live-super-secret-value");

        Assert.Equal("***REDACTED***", redacted);
        Assert.DoesNotContain("secret", redacted, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
