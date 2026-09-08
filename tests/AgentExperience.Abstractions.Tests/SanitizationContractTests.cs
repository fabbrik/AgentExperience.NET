namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Proves the <see cref="ISanitizer"/> port supports both a redacting implementation and a
/// rejecting implementation without changing shape (AC3): sanitization only fixes a
/// redact-or-reject contract, never a specific policy.
/// </summary>
public class SanitizationContractTests
{
    /// <summary>A fake that redacts any field named "secret" and omits any field it does not recognize, allowing the rest of the payload through.</summary>
    private sealed class RedactingFakeSanitizer : ISanitizer
    {
        private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase) { "query", "ticketId", "secret" };

        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
        {
            var fields = new Dictionary<string, object?>();
            var redactedPaths = new List<string>();
            var omittedPaths = new List<string>();

            foreach (var (key, value) in payload.Fields)
            {
                if (!KnownFields.Contains(key))
                {
                    // Unknown fields are omitted by default rather than passed through.
                    omittedPaths.Add(key);
                    continue;
                }

                if (string.Equals(key, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    fields[key] = "***REDACTED***";
                    redactedPaths.Add(key);
                    continue;
                }

                fields[key] = value;
            }

            return Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Allowed,
                Fields: fields,
                RedactedFieldPaths: redactedPaths,
                OmittedFieldPaths: omittedPaths,
                Reason: null));
        }
    }

    /// <summary>A fake that fail-closed rejects any payload containing a field named "secret".</summary>
    private sealed class RejectingFakeSanitizer : ISanitizer
    {
        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
        {
            if (payload.Fields.ContainsKey("secret"))
            {
                return Task.FromResult(new SanitizedPayload(
                    Decision: SanitizationDecision.Rejected,
                    Fields: new Dictionary<string, object?>(),
                    RedactedFieldPaths: [],
                    OmittedFieldPaths: [],
                    Reason: "payload contains an unredactable secret field"));
            }

            return Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Allowed,
                Fields: payload.Fields,
                RedactedFieldPaths: [],
                OmittedFieldPaths: [],
                Reason: null));
        }
    }

    private static readonly RawPayload PayloadWithSecret = new(
        Kind: "ToolArguments",
        Fields: new Dictionary<string, object?>
        {
            ["query"] = "refund policy",
            ["secret"] = "sk-super-secret-token",
            ["undeclaredField"] = "unexpected-value",
        });

    [Fact]
    public async Task Redacting_implementation_fits_the_ISanitizer_port_and_allows_the_payload_through_with_sensitive_fields_redacted()
    {
        ISanitizer sanitizer = new RedactingFakeSanitizer();

        var result = await sanitizer.SanitizeAsync(PayloadWithSecret);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
        Assert.Equal("refund policy", result.Fields["query"]);
        Assert.Equal("***REDACTED***", result.Fields["secret"]);
        Assert.False(result.Fields.ContainsKey("undeclaredField"));

        // "secret" kept a (masked) value: redacted, not omitted.
        Assert.Contains("secret", result.RedactedFieldPaths);
        Assert.DoesNotContain("secret", result.OmittedFieldPaths);

        // "undeclaredField" was dropped entirely: omitted, not redacted.
        Assert.Contains("undeclaredField", result.OmittedFieldPaths);
        Assert.DoesNotContain("undeclaredField", result.RedactedFieldPaths);
    }

    [Fact]
    public async Task Rejecting_implementation_fits_the_ISanitizer_port_and_fail_closed_denies_the_payload()
    {
        ISanitizer sanitizer = new RejectingFakeSanitizer();

        var result = await sanitizer.SanitizeAsync(PayloadWithSecret);

        Assert.Equal(SanitizationDecision.Rejected, result.Decision);
        Assert.Empty(result.Fields);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task Rejecting_implementation_still_allows_payloads_that_do_not_trip_its_policy()
    {
        ISanitizer sanitizer = new RejectingFakeSanitizer();
        var cleanPayload = new RawPayload("ToolArguments", new Dictionary<string, object?> { ["query"] = "refund policy" });

        var result = await sanitizer.SanitizeAsync(cleanPayload);

        Assert.Equal(SanitizationDecision.Allowed, result.Decision);
    }
}
