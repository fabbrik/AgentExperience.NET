namespace AgentExperience.Core.Tests;

/// <summary>
/// A second, independent, minimal <see cref="ISanitizer"/> implementation that rejects every
/// payload unconditionally -- deliberately not built on <c>DefaultSanitizer</c>,
/// <c>SanitizationOptions</c>, or any of <c>AgentExperience.Core</c>'s traversal code; it only
/// implements the <see cref="ISanitizer"/> port itself. Inheriting
/// <see cref="SanitizerConformanceTests"/> here (AC3) proves the shared fixtures exercise the
/// <see cref="ISanitizer"/> port's fail-closed safety contract, not <c>DefaultSanitizer</c>'s
/// specific policy or implementation quirks: an implementation that never allows anything through
/// still satisfies every fixture, because none of them require a payload to be <c>Allowed</c>.
/// </summary>
public class FakeRejectAllSanitizerTests : SanitizerConformanceTests
{
    private sealed class FakeRejectAllSanitizer : ISanitizer
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyFields = new Dictionary<string, object?>();

        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Rejected,
                Fields: EmptyFields,
                RedactedFieldPaths: [],
                OmittedFieldPaths: [],
                Reason: "this fake sanitizer rejects every payload unconditionally, regardless of Kind or content"));
    }

    protected override string Kind => "AnyKindAtAll";

    protected override ISanitizer CreateSanitizer() => new FakeRejectAllSanitizer();
}
