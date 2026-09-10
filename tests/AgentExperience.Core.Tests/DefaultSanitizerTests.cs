using System.Collections;
using System.Dynamic;
using System.Text.Json;
using Microsoft.Extensions.Compliance.Redaction;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Exercises <see cref="DefaultSanitizer"/> specifically: inherits every
/// <see cref="SanitizerConformanceTests"/> fixture (AC1, AC2, AC3), then adds
/// <see cref="DefaultSanitizer"/>-specific coverage -- the full AC1 scenario (allowlisted,
/// secret-classified, and unknown fields nested at multiple depths, including a secret-classified
/// dict/list value and a list of nested dicts) asserted with concrete positive values, exact
/// limit-boundary cases, an unconfigured-<c>Kind</c> case, redactor injection, and a direct check
/// that a rejection's <see cref="SanitizedPayload.Reason"/> -- which is exactly the message of the
/// <see cref="SanitizationFailureException"/> thrown-and-caught internally during traversal --
/// never contains an input field's raw value (AC2).
/// </summary>
public class DefaultSanitizerTests : SanitizerConformanceTests
{
    private const string ToolArgumentsKind = "ToolArguments";
    private const string BoundaryKind = "Boundary";
    private const string UnconfiguredKind = "TotallyUnconfiguredKind";

    private static readonly SanitizationPolicy ToolArgumentsPolicy = new(
        AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "tool", "note", "nested", "items" },
        SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { "apiKey", "authToken", "credentials" },
        MaxDepth: 5,
        MaxFieldCount: 10,
        MaxValueLength: 200,
        MaxFieldNameLength: 64);

    // Deliberately tiny limits so exact-boundary and one-over-boundary payloads stay small and
    // legible -- kept on a separate Kind so these cases can't interact with ToolArgumentsPolicy.
    // MaxFieldNameLength is intentionally its own, larger number (not reusing MaxValueLength):
    // field names used throughout these tests ("nested", "field0".."field3") are 6 characters,
    // which must stay comfortably under the field-*name* limit even while MaxValueLength itself
    // stays tiny for legible value-length boundary tests.
    private static readonly SanitizationPolicy BoundaryPolicy = new(
        AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "a", "nested" },
        SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { "secret" },
        MaxDepth: 2,
        MaxFieldCount: 3,
        MaxValueLength: 5,
        MaxFieldNameLength: 10);

    private static readonly SanitizationOptions TestOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        [ToolArgumentsKind] = ToolArgumentsPolicy,
        [BoundaryKind] = BoundaryPolicy,
    });

    protected override string Kind => ToolArgumentsKind;

    protected override ISanitizer CreateSanitizer() => new DefaultSanitizer(TestOptions);

    [Fact]
    public async Task AC1_unknown_fields_are_omitted_secret_fields_are_redacted_whole_and_allowlisted_fields_pass_through_or_recurse()
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?>
        {
            // Allowlisted scalar at depth 1: passes through unchanged.
            ["tool"] = "send_email",

            // Unknown field at depth 1: omitted entirely.
            ["topLevelUnknown"] = "raw-unknown-top-level-value",

            // Secret-classified field at depth 1 whose value is a dict: redacted whole, never traversed.
            ["credentials"] = new Dictionary<string, object?>
            {
                ["user"] = "alice",
                ["apiKey"] = "sk-nested-in-credentials-dict",
            },

            // Secret-classified field at depth 1 whose value is a list: redacted whole, never traversed.
            ["authToken"] = new List<object?> { "token-part-1", "token-part-2" },

            // Allowlisted field at depth 1 whose value is a dict: recursed one level deeper.
            ["nested"] = new Dictionary<string, object?>
            {
                ["note"] = "inner allowed value",
                ["apiKey"] = "sk-depth-2-secret",
                ["unexpectedField"] = "raw-unknown-depth-2-value",
            },

            // Allowlisted field at depth 1 whose value is a list of nested dicts: each item recursed.
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["note"] = "item0 note", ["apiKey"] = "sk-item0-secret" },
                new Dictionary<string, object?> { ["note"] = "item1 note", ["reallyUnknown"] = "raw-unknown-item1-value" },
            },
        });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.Null(result.Reason);

        // Allowlisted scalar: passed through unchanged.
        Assert.Equal("send_email", result.Fields["tool"]);

        // Unknown top-level field: omitted, not present at all.
        Assert.False(result.Fields.ContainsKey("topLevelUnknown"));
        Assert.Contains("topLevelUnknown", result.OmittedFieldPaths);

        // Secret dict value: redacted as a single unit -- present, but collapsed to a redacted
        // string, not still a dict, and no trace of its content.
        Assert.True(result.Fields.ContainsKey("credentials"));
        var redactedCredentials = Assert.IsType<string>(result.Fields["credentials"]);
        Assert.DoesNotContain("alice", redactedCredentials);
        Assert.DoesNotContain("sk-nested-in-credentials-dict", redactedCredentials);
        Assert.Contains("credentials", result.RedactedFieldPaths);
        // Never traversed: no sub-path of "credentials" was independently classified.
        Assert.DoesNotContain(result.RedactedFieldPaths, p => p.StartsWith("credentials.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.OmittedFieldPaths, p => p.StartsWith("credentials.", StringComparison.Ordinal));

        // Secret list value: redacted as a single unit -- present, but collapsed to a redacted
        // string, not still a list.
        Assert.True(result.Fields.ContainsKey("authToken"));
        Assert.IsType<string>(result.Fields["authToken"]);
        Assert.Contains("authToken", result.RedactedFieldPaths);

        // Allowlisted dict recursed: inner allowed field passes through, inner secret redacted (kept, not
        // dropped), inner unknown omitted (dropped, not kept).
        var nested = Assert.IsType<Dictionary<string, object?>>(result.Fields["nested"]);
        Assert.Equal("inner allowed value", nested["note"]);
        Assert.True(nested.ContainsKey("apiKey"));
        Assert.NotEqual("sk-depth-2-secret", nested["apiKey"]);
        Assert.False(nested.ContainsKey("unexpectedField"));
        Assert.Contains("nested.apiKey", result.RedactedFieldPaths);
        Assert.Contains("nested.unexpectedField", result.OmittedFieldPaths);

        // Allowlisted list of dicts recursed: each item classified independently.
        var items = Assert.IsType<List<object?>>(result.Fields["items"]);
        Assert.Equal(2, items.Count);
        var item0 = Assert.IsType<Dictionary<string, object?>>(items[0]);
        Assert.Equal("item0 note", item0["note"]);
        Assert.True(item0.ContainsKey("apiKey"));
        Assert.NotEqual("sk-item0-secret", item0["apiKey"]);
        var item1 = Assert.IsType<Dictionary<string, object?>>(items[1]);
        Assert.Equal("item1 note", item1["note"]);
        Assert.False(item1.ContainsKey("reallyUnknown"));
        Assert.Contains("items[0].apiKey", result.RedactedFieldPaths);
        Assert.Contains("items[1].reallyUnknown", result.OmittedFieldPaths);
    }

    [Fact]
    public async Task Unconfigured_Kind_defaults_to_reject_everything()
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(UnconfiguredKind, new Dictionary<string, object?> { ["tool"] = "send_email" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains(UnconfiguredKind, result.Reason);
    }

    [Theory]
    [InlineData(2, true)]  // exactly at MaxDepth: allowed
    [InlineData(3, false)] // one level beyond MaxDepth: rejected
    public async Task Depth_limit_boundary(int actualDepth, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        // Builds a chain of dicts nested under "nested" whose innermost dict (a leaf field "a")
        // sits exactly at container-depth `actualDepth`, counting the root itself as depth 1.
        var rootFields = BuildNestedAtDepth(currentDepth: 1, targetDepth: actualDepth);

        var payload = new RawPayload(BoundaryKind, rootFields);
        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (!expectAllowed)
        {
            Assert.Empty(result.Fields);
        }
    }

    private static Dictionary<string, object?> BuildNestedAtDepth(int currentDepth, int targetDepth) =>
        currentDepth == targetDepth
            ? new Dictionary<string, object?> { ["a"] = "leaf" }
            : new Dictionary<string, object?> { ["nested"] = BuildNestedAtDepth(currentDepth + 1, targetDepth) };

    // SanitizeList carries its own copies of the depth/field-count/value-length guards (not just
    // SanitizeFields' copies) -- these three tests route each limit kind through a list at some
    // point in the chain, so a future regression that drops one of SanitizeList's own guard calls
    // would fail here even though every dict-only test above still passes.

    [Fact]
    public async Task Depth_limit_boundary_via_list_exactly_at_limit_is_allowed()
    {
        var sanitizer = CreateSanitizer();
        // root(depth1) -> "nested" is a list of leaf values: the list itself is depth2, and none
        // of its items recurse further, so this sits exactly at BoundaryPolicy.MaxDepth (2).
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["nested"] = new List<object?> { "x", "y" } });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
    }

    [Fact]
    public async Task Depth_limit_boundary_via_list_one_over_limit_is_rejected()
    {
        var sanitizer = CreateSanitizer();
        // root(depth1) -> "nested" is a list(depth2) containing a dict(depth3) -- one level beyond
        // BoundaryPolicy.MaxDepth (2), and the violation is only reachable through SanitizeList's
        // own recursive item handling, not SanitizeFields'.
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?>
        {
            ["nested"] = new List<object?> { new Dictionary<string, object?> { ["a"] = "leaf" } },
        });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
    }

    [Theory]
    [InlineData(3, true)]  // exactly at MaxFieldCount: allowed
    [InlineData(4, false)] // one more than MaxFieldCount: rejected
    public async Task Field_count_limit_boundary_via_list(int itemCount, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        // The root dict itself has a single field ("nested"), well within MaxFieldCount -- only
        // the list's own item count (checked by SanitizeList) can trip this.
        var items = Enumerable.Range(0, itemCount).Select(i => (object?)$"item{i}").ToList();
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["nested"] = items });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (!expectAllowed)
        {
            Assert.Empty(result.Fields);
        }
    }

    [Theory]
    [InlineData(5, true)]  // exactly at MaxValueLength: allowed
    [InlineData(6, false)] // one character beyond MaxValueLength: rejected
    public async Task Value_length_limit_boundary_via_list(int valueLength, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?>
        {
            ["nested"] = new List<object?> { new string('x', valueLength) },
        });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (!expectAllowed)
        {
            Assert.Empty(result.Fields);
        }
    }

    [Fact]
    public async Task A_lazily_evaluated_over_limit_sequence_rejects_promptly_without_full_materialization()
    {
        var sanitizer = CreateSanitizer();
        // Throws if enumerated past 1000 items. BoundaryPolicy.MaxFieldCount is 3, so a correct,
        // incrementally-checking implementation rejects after consuming only 4 items and never
        // gets anywhere near 1000 -- an implementation that eagerly materializes the whole
        // sequence first (e.g. via .ToList()) would instead surface this type's own exception
        // uncaught, failing this test.
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["nested"] = new ThrowIfFullyEnumeratedSequence(hangAfter: 1000) });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
    }

    /// <summary>A lazily-evaluated sequence that throws if enumeration ever reaches <c>hangAfter</c> items -- stands in for an effectively-infinite lazy <see cref="IEnumerable"/> without risking an actual test hang.</summary>
    private sealed class ThrowIfFullyEnumeratedSequence(int hangAfter) : IEnumerable<object?>
    {
        public IEnumerator<object?> GetEnumerator()
        {
            var i = 0;
            while (true)
            {
                if (i >= hangAfter)
                {
                    throw new InvalidOperationException("this sequence must never be fully enumerated -- it is effectively infinite");
                }

                yield return $"item{i}";
                i++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task Byte_array_is_treated_as_an_opaque_leaf_checked_against_value_length_not_field_count()
    {
        var sanitizer = CreateSanitizer();
        // 4 bytes: exceeds BoundaryPolicy.MaxFieldCount (3) if wrongly reshaped into a list of
        // individually-counted items, but is within BoundaryPolicy.MaxValueLength (5) when
        // correctly treated as a single opaque leaf.
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["a"] = new byte[4] });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        var bytes = Assert.IsType<byte[]>(result.Fields["a"]);
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public async Task Byte_array_longer_than_MaxValueLength_is_rejected()
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["a"] = new byte[6] });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
    }

    [Fact]
    public async Task JsonElement_object_is_recursed_field_by_field_and_its_secrets_are_redacted()
    {
        var sanitizer = CreateSanitizer();
        using var document = JsonDocument.Parse("""{"note":"hello","apiKey":"sk-json-secret-value"}""");
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["nested"] = document.RootElement });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        var nested = Assert.IsType<Dictionary<string, object?>>(result.Fields["nested"]);
        Assert.Equal("hello", ((JsonElement)nested["note"]!).GetString());
        Assert.True(nested.ContainsKey("apiKey"));
        Assert.DoesNotContain("sk-json-secret-value", nested["apiKey"]!.ToString());
        Assert.Contains("nested.apiKey", result.RedactedFieldPaths);
    }

    [Fact]
    public async Task JsonElement_array_is_recursed_item_by_item_and_its_secrets_are_redacted()
    {
        var sanitizer = CreateSanitizer();
        using var document = JsonDocument.Parse("""[{"note":"first","apiKey":"sk-json-array-secret"},{"note":"second"}]""");
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["items"] = document.RootElement });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        var items = Assert.IsType<List<object?>>(result.Fields["items"]);
        Assert.Equal(2, items.Count);
        var item0 = Assert.IsType<Dictionary<string, object?>>(items[0]);
        Assert.DoesNotContain("sk-json-array-secret", item0["apiKey"]!.ToString());
        Assert.Contains("items[0].apiKey", result.RedactedFieldPaths);
    }

    [Fact]
    public async Task Unrecognized_reference_type_value_is_omitted_not_passed_through_unexamined()
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["note"] = new OpaqueSecretHolder() });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.False(result.Fields.ContainsKey("note"));
        Assert.Contains("note", result.OmittedFieldPaths);
    }

    /// <summary>A reference type unrecognized by any of <see cref="DefaultSanitizer"/>'s classified shapes, whose default <see cref="object.ToString()"/> would leak its secret if ever blindly stringified/passed through.</summary>
    private sealed class OpaqueSecretHolder
    {
        private const string Secret = "sk-should-never-leak-via-tostring-fallback";

        public override string ToString() => Secret;
    }

    [Fact]
    public async Task Value_type_scalar_still_passes_through_via_the_default_fallback()
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["note"] = 42 });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.Equal(42, result.Fields["note"]);
    }

    [Fact]
    public async Task Dictionary_of_string_string_is_classified_field_by_field_not_treated_as_a_list()
    {
        var sanitizer = CreateSanitizer();
        var legacyDict = new Dictionary<string, string> { ["note"] = "hello", ["apiKey"] = "sk-legacy-dict-secret" };
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["nested"] = legacyDict });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        var nested = Assert.IsType<Dictionary<string, object?>>(result.Fields["nested"]);
        Assert.Equal("hello", nested["note"]);
        Assert.True(nested.ContainsKey("apiKey"));
        Assert.NotEqual("sk-legacy-dict-secret", nested["apiKey"]);
        Assert.Contains("nested.apiKey", result.RedactedFieldPaths);
    }

    [Fact]
    public async Task ExpandoObject_is_classified_field_by_field_not_treated_as_a_list()
    {
        var sanitizer = CreateSanitizer();
        dynamic expando = new ExpandoObject();
        expando.note = "hello";
        expando.apiKey = "sk-expando-secret";
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["nested"] = (object)expando });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        var nested = Assert.IsType<Dictionary<string, object?>>(result.Fields["nested"]);
        Assert.Equal("hello", nested["note"]);
        Assert.True(nested.ContainsKey("apiKey"));
        Assert.NotEqual("sk-expando-secret", nested["apiKey"]);
        Assert.Contains("nested.apiKey", result.RedactedFieldPaths);
    }

    [Fact]
    public async Task A_throwing_injected_Redactor_results_in_a_graceful_Rejected_result_not_an_uncaught_exception()
    {
        var sanitizer = new DefaultSanitizer(TestOptions, new ThrowingRedactor());
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["apiKey"] = "sk-secret" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains(nameof(SanitizationFailureReason.RedactorFailed), result.Reason);
    }

    /// <summary>A <see cref="Redactor"/> that always throws -- proves a misbehaving injected redactor degrades to a graceful <see cref="SanitizationDecision.Rejected"/> result rather than an uncaught exception. Both members throw, since <see cref="Redactor.Redact(string?)"/>'s own implementation may call either first depending on input.</summary>
    private sealed class ThrowingRedactor : Redactor
    {
        public override int GetRedactedLength(ReadOnlySpan<char> input) => throw new InvalidOperationException("this redactor always fails");

        public override int Redact(ReadOnlySpan<char> source, Span<char> destination) => throw new InvalidOperationException("this redactor always fails");
    }

    [Fact]
    public async Task A_field_name_containing_a_path_delimiter_character_is_rejected_fail_closed_not_silently_merged_into_a_colliding_path()
    {
        var sanitizer = CreateSanitizer();
        // Demonstrates the exact collision risk: this single key, if it were used to build a path
        // verbatim, would be textually indistinguishable from a genuinely nested "unknownReal"
        // field under item 0 of an "items" list.
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["items[0].unknownReal"] = "irrelevant" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
    }

    [Theory]
    [InlineData(10, true)]  // exactly at BoundaryPolicy.MaxFieldNameLength: allowed
    [InlineData(11, false)] // one character beyond: rejected
    public async Task Field_name_length_boundary(int nameLength, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        var longKey = new string('k', nameLength);
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { [longKey] = "x" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (!expectAllowed)
        {
            Assert.Empty(result.Fields);
        }
    }

    [Theory]
    [InlineData(3, true)]  // exactly at MaxFieldCount: allowed
    [InlineData(4, false)] // one more than MaxFieldCount: rejected
    public async Task Field_count_limit_boundary(int fieldCount, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        var fields = new Dictionary<string, object?>();
        for (var i = 0; i < fieldCount; i++)
        {
            fields[$"field{i}"] = "x";
        }

        var payload = new RawPayload(BoundaryKind, fields);
        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (!expectAllowed)
        {
            Assert.Empty(result.Fields);
        }
    }

    [Theory]
    [InlineData(5, true)]  // exactly at MaxValueLength: allowed
    [InlineData(6, false)] // one character beyond MaxValueLength: rejected
    public async Task Value_length_limit_boundary(int valueLength, bool expectAllowed)
    {
        var sanitizer = CreateSanitizer();
        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["a"] = new string('x', valueLength) });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(expectAllowed ? SanitizationDecision.Allowed : SanitizationDecision.Rejected, result.Decision);
        if (expectAllowed)
        {
            Assert.Equal(new string('x', valueLength), result.Fields["a"]);
        }
        else
        {
            Assert.Empty(result.Fields);
        }
    }

    [Fact]
    public void SanitizationFailureException_message_is_built_only_from_safe_identifiers_never_a_value()
    {
        // There is no constructor parameter through which a raw field *value* could ever reach
        // this type -- only a Kind, a classification, and a field *path* built from field names.
        var exception = new SanitizationFailureException(ToolArgumentsKind, SanitizationFailureReason.ValueLengthLimitExceeded, "nested.apiKey");

        Assert.Contains(ToolArgumentsKind, exception.Message);
        Assert.Contains(nameof(SanitizationFailureReason.ValueLengthLimitExceeded), exception.Message);
        Assert.Contains("nested.apiKey", exception.Message); // a field *path* (names only) is a safe identifier.
    }

    [Fact]
    public async Task Rejection_reason_is_the_internally_thrown_SanitizationFailureException_message_and_never_contains_the_input_value()
    {
        var sanitizer = CreateSanitizer();
        const string oversizedMarker = "this-exact-string-must-never-appear-in-the-reason-0f3c9a";
        Assert.True(oversizedMarker.Length > BoundaryPolicy.MaxValueLength);

        var payload = new RawPayload(BoundaryKind, new Dictionary<string, object?> { ["a"] = oversizedMarker });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.NotNull(result.Reason);
        Assert.DoesNotContain(oversizedMarker, result.Reason);
        // The reason is still a meaningful, safe explanation -- not merely empty.
        Assert.Contains(BoundaryKind, result.Reason);
        Assert.Contains(nameof(SanitizationFailureReason.ValueLengthLimitExceeded), result.Reason);
        Assert.Contains("at field 'a'", result.Reason); // the field *path* is a safe identifier.
    }

    [Fact]
    public async Task Default_redactor_is_ErasingRedactor_when_none_is_injected()
    {
        var sanitizer = new DefaultSanitizer(TestOptions);
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["apiKey"] = "sk-should-never-survive" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.NotEqual("sk-should-never-survive", result.Fields["apiKey"]);
    }

    [Fact]
    public async Task A_custom_Redactor_can_be_injected_via_the_constructor()
    {
        var sanitizer = new DefaultSanitizer(TestOptions, new FixedMaskRedactor());
        var payload = new RawPayload(ToolArgumentsKind, new Dictionary<string, object?> { ["apiKey"] = "sk-should-never-survive" });

        var result = await sanitizer.SanitizeAsync(payload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.Equal(FixedMaskRedactor.Mask, result.Fields["apiKey"]);
    }

    /// <summary>A minimal custom <see cref="Redactor"/> used only to prove <see cref="DefaultSanitizer"/> composes whatever <see cref="Redactor"/> it is given.</summary>
    private sealed class FixedMaskRedactor : Redactor
    {
        public const string Mask = "***REDACTED***";

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
}
