using System.Collections;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Reusable conformance fixtures every fail-closed <see cref="ISanitizer"/> implementation must
/// satisfy (AC1, AC2, AC3): a subclass supplies the <see cref="ISanitizer"/> under test via
/// <see cref="CreateSanitizer"/>. These fixtures prove the <see cref="ISanitizer"/> port's safety
/// contract -- a raw secret value, a raw unknown-field value, or an over-limit value must never
/// surface in a <see cref="SanitizedPayload"/> result or in any thrown exception's message -- not
/// one specific implementation's policy choices (AC3): every assertion here is a negative/safety
/// assertion ("the raw value never leaks anywhere"), deliberately never a positive claim like "the
/// field must be present and the decision must be Allowed", because only a specific policy (like
/// <c>DefaultSanitizer</c>'s own, asserted separately in <c>DefaultSanitizerTests</c>) can promise
/// that. That is what lets both <c>DefaultSanitizer</c> (which redacts-and-allows) and the
/// independent, unconditionally-rejecting fake in <c>FakeRejectAllSanitizerTests</c> satisfy the
/// exact same fixtures.
/// </summary>
public abstract class SanitizerConformanceTests
{
    private const string SecretMarker = "sk-conformance-secret-3f9c1a";
    private const string UnknownFieldMarker = "conformance-unknown-field-8b2d";
    private const string NestedDictSecretMarker = "conformance-nested-dict-secret-77aa";
    private const string NestedListSecretMarker = "conformance-nested-list-secret-c40e";
    private const string ListOfDictsSecretMarker = "conformance-list-of-dicts-secret-91fe";

    /// <summary>The <c>Kind</c> to submit test payloads under.</summary>
    protected abstract string Kind { get; }

    /// <summary>Creates a fresh instance of the <see cref="ISanitizer"/> under test.</summary>
    protected abstract ISanitizer CreateSanitizer();

    [Fact]
    public async Task Classified_leaf_at_depth_never_leaks_its_raw_value()
    {
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["note"] = "hello",
            ["credentials"] = new Dictionary<string, object?>
            {
                ["apiKey"] = SecretMarker,
            },
        });

        var (result, thrown) = await RunAsync(payload);

        AssertNoRawValueLeak(SecretMarker, result, thrown);
    }

    [Fact]
    public async Task Unknown_field_at_depth_never_leaks_its_raw_value()
    {
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["note"] = "hello",
            ["nested"] = new Dictionary<string, object?>
            {
                ["completelyUnrecognizedField"] = UnknownFieldMarker,
            },
        });

        var (result, thrown) = await RunAsync(payload);

        AssertNoRawValueLeak(UnknownFieldMarker, result, thrown);
    }

    [Fact]
    public async Task Secret_classified_dict_value_is_never_traversed_or_leaked()
    {
        // "credentials" is secret-classified: the whole dict must be redacted as a unit, never
        // traversed -- so even a plain-looking key nested inside it must never surface, whether
        // redacted or passed through.
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["credentials"] = new Dictionary<string, object?>
            {
                ["note"] = NestedDictSecretMarker,
            },
        });

        var (result, thrown) = await RunAsync(payload);

        AssertNoRawValueLeak(NestedDictSecretMarker, result, thrown);
    }

    [Fact]
    public async Task Secret_classified_list_value_is_never_traversed_or_leaked()
    {
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["credentials"] = new List<object?> { NestedListSecretMarker, "another-item" },
        });

        var (result, thrown) = await RunAsync(payload);

        AssertNoRawValueLeak(NestedListSecretMarker, result, thrown);
    }

    [Fact]
    public async Task List_of_nested_dicts_is_recursed_and_its_secrets_never_leak()
    {
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["note"] = "first", ["apiKey"] = ListOfDictsSecretMarker },
                new Dictionary<string, object?> { ["note"] = "second" },
            },
        });

        var (result, thrown) = await RunAsync(payload);

        AssertNoRawValueLeak(ListOfDictsSecretMarker, result, thrown);
    }

    [Fact]
    public async Task Limit_violation_rejects_fail_closed_with_no_payload_anywhere_in_result_or_thrown_exceptions()
    {
        var hugeValue = new string('x', 1_000_000);
        var payload = new RawPayload(Kind, new Dictionary<string, object?>
        {
            ["note"] = hugeValue,
        });

        var (result, thrown) = await RunAsync(payload);

        if (thrown is not null)
        {
            Assert.DoesNotContain(hugeValue, thrown.Message);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(SanitizationDecision.Rejected, result!.Decision);
        Assert.Empty(result.Fields);
        AssertNoRawValueLeak(hugeValue, result, thrown: null);
    }

    private async Task<(SanitizedPayload? Result, Exception? Thrown)> RunAsync(RawPayload payload)
    {
        var sanitizer = CreateSanitizer();

        try
        {
            var result = await sanitizer.SanitizeAsync(payload);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static void AssertNoRawValueLeak(string rawMarker, SanitizedPayload? result, Exception? thrown)
    {
        if (thrown is not null)
        {
            Assert.DoesNotContain(rawMarker, thrown.Message);
            return;
        }

        Assert.NotNull(result);

        if (result!.Decision == SanitizationDecision.Rejected)
        {
            Assert.Empty(result.Fields);
        }

        Assert.DoesNotContain(rawMarker, Dump(result.Fields));
        Assert.DoesNotContain(rawMarker, result.Reason ?? string.Empty);
    }

    private static string Dump(IReadOnlyDictionary<string, object?> fields) =>
        string.Join('|', fields.Select(kvp => $"{kvp.Key}={DumpValue(kvp.Value)}"));

    private static string DumpValue(object? value) => value switch
    {
        null => "null",
        IReadOnlyDictionary<string, object?> dict => Dump(dict),
        string s => s,
        IEnumerable list => string.Join(',', list.Cast<object?>().Select(DumpValue)),
        _ => value.ToString() ?? string.Empty,
    };
}
