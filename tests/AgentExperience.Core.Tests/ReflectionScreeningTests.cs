using System.Text.Json;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Tests.Diagnostics;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 14.1: finalization screens what a reflector wrote -- limits, invisible characters, and the
/// host's sanitizer -- before the record is created. Every test goes through the real finalization
/// path: real capture, the real default reflector (wrapped where a hostile one is needed), and the real
/// lifecycle service over an in-memory store.
/// </summary>
public class ReflectionScreeningTests
{
    internal const string Marker = "SCREENING-MARKER-7f3a";

    /// <summary>A marker a type name can carry, so "no exception type name reaches a reason" is tested too.</summary>
    internal const string TypeMarker = "ScreeningTypeMarker7f3a";

    private static readonly string[] ListFields =
    [
        nameof(Reflection.SuccessfulApproaches),
        nameof(Reflection.FailedApproaches),
        nameof(Reflection.Preconditions),
        nameof(Reflection.Warnings),
    ];

    // ---------------------------------------------------------------------------------------------
    // The default reflector passes unchanged
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string> RunShapes => ["one-attempt", "failed-then-succeeded", "quoted-errors", "unknown-preconditions", "many-tools"];

    [Theory]
    [MemberData(nameof(RunShapes))]
    public async Task The_default_reflectors_output_is_stored_byte_for_byte(string shape)
    {
        foreach (var sanitizer in new ISanitizer?[] { null, new DefaultSanitizer(ScreeningHarness.CaptureOptions) })
        {
            var recorder = new RecordingReflector(new DefaultExperienceReflector());
            var harness = await ScreeningHarness.WithRunAsync(recorder, shape, sanitizer);

            var result = await harness.FinalizeAsync();

            Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
            var produced = Assert.Single(recorder.Produced);
            var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
            Assert.Equal(Json(produced), Json(stored));
            Assert.Equal(Json(produced), Json(result.Reflection!));
            Assert.Empty(result.ReflectionRedactedFieldPaths);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Limits
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string> OverLimitCases => new()
    {
        { "lesson", nameof(Reflection.Lesson) },
        { "guidance", nameof(Reflection.ReuseGuidance) },
        { "producer", nameof(Reflection.Producer) },
        { "item:" + nameof(Reflection.SuccessfulApproaches), nameof(Reflection.SuccessfulApproaches) + "[0]" },
        { "item:" + nameof(Reflection.FailedApproaches), nameof(Reflection.FailedApproaches) + "[0]" },
        { "item:" + nameof(Reflection.Preconditions), nameof(Reflection.Preconditions) + "[0]" },
        { "item:" + nameof(Reflection.Warnings), nameof(Reflection.Warnings) + "[0]" },
        { "count:" + nameof(Reflection.SuccessfulApproaches), nameof(Reflection.SuccessfulApproaches) },
        { "count:" + nameof(Reflection.FailedApproaches), nameof(Reflection.FailedApproaches) },
        { "count:" + nameof(Reflection.Preconditions), nameof(Reflection.Preconditions) },
        { "count:" + nameof(Reflection.Warnings), nameof(Reflection.Warnings) },
    };

    [Theory]
    [MemberData(nameof(OverLimitCases))]
    public async Task A_reflection_over_a_limit_is_refused_and_the_record_quarantined(string violation, string field)
    {
        var limits = ReflectionLimits.Default;
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => Violate(reflection, violation, limits.MaxLessonLength + 1, limits.MaxListItemLength + 1, limits.MaxListItems + 1, limits.MaxProducerLength + 1)));

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.OverLimit);
        Assert.Contains(field, result.Failure!.Reason, StringComparison.Ordinal);
        Assert.Contains("limit", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OverLimitCases))]
    public async Task A_reflection_exactly_at_every_limit_is_stored(string violation, string field)
    {
        _ = field;
        var limits = ReflectionLimits.Default;
        var recorder = new RecordingReflector(new RewritingReflector(reflection => Violate(reflection, violation, limits.MaxLessonLength, limits.MaxListItemLength, limits.MaxListItems, limits.MaxProducerLength)));
        var harness = await ScreeningHarness.WithRunAsync(recorder);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(Json(Assert.Single(recorder.Produced)), Json(result.Record!.Reflection!));
    }

    [Fact]
    public async Task Over_limit_text_is_refused_rather_than_truncated_under_host_limits()
    {
        var options = new ExperienceFinalizationOptions { ReflectionLimits = new ReflectionLimits(MaxLessonLength: 10) };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), options: options);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.OverLimit);
        Assert.Contains("Lesson is longer than the 10-character limit", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_limits_can_also_be_raised()
    {
        var options = new ExperienceFinalizationOptions { ReflectionLimits = ReflectionLimits.Default with { MaxListItems = 40 } };
        var harness = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with { Warnings = [.. Enumerable.Range(0, 40).Select(i => $"warning {i}")] }),
            options: options);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(40, result.Record!.Reflection!.Warnings.Count);
    }

    [Fact]
    public void Limits_must_be_strictly_positive()
    {
        Assert.Equal(4_000, ReflectionLimits.Default.MaxLessonLength);
        Assert.Equal(1_000, ReflectionLimits.Default.MaxListItemLength);
        Assert.Equal(32, ReflectionLimits.Default.MaxListItems);
        Assert.Equal(200, ReflectionLimits.Default.MaxProducerLength);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ReflectionLimits(MaxLessonLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReflectionLimits(MaxListItemLength: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReflectionLimits.Default with { MaxListItems = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => ReflectionLimits.Default with { MaxProducerLength = 0 });
        Assert.Throws<ArgumentNullException>(() => new ExperienceFinalizationOptions { ReflectionLimits = null! });
        Assert.Same(ReflectionLimits.Default, ExperienceFinalizationOptions.Default.ReflectionLimits);
    }

    [Fact]
    public async Task A_host_list_that_changes_after_it_was_checked_cannot_put_unchecked_text_on_the_record()
    {
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with
        {
            Warnings = new FlippingList<string>(["checked"], ["checked", new string('x', 5_000)]),
        }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(["checked"], harness.Store.Find(result.Record!.ExperienceId)!.Reflection!.Warnings);
    }

    // ---------------------------------------------------------------------------------------------
    // Neutralization
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Control_invisible_and_bidirectional_characters_are_removed_from_every_free_text_field()
    {
        // RLO, ZWSP, BEL, a private-use character, a TAG letter, an unassigned code point, a lone
        // surrogate, and a tab and a line feed, which are whitespace and so become spaces.
        const string Dirty = "a\u202Eb\u200Bc\u0007d\uE000e\U000E0041f\u0378g\uD800h\ti\nj\u2066k\u2069";
        const string Clean = "abcdefgh i jk";

        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with
        {
            Lesson = "Lesson " + Dirty,
            SuccessfulApproaches = [Dirty],
            FailedApproaches = [Dirty],
            Preconditions = [Dirty],
            Warnings = [Dirty, "plain"],
            ReuseGuidance = Dirty,
        }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
        Assert.Equal("Lesson " + Clean, stored.Lesson);
        Assert.Equal([Clean], stored.SuccessfulApproaches);
        Assert.Equal([Clean], stored.FailedApproaches);
        Assert.Equal([Clean], stored.Preconditions);
        Assert.Equal([Clean, "plain"], stored.Warnings);
        Assert.Equal(Clean, stored.ReuseGuidance);
    }

    [Fact]
    public async Task Neutralization_runs_before_the_limit_so_invisible_padding_does_not_count()
    {
        var limits = ReflectionLimits.Default;
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with
        {
            Lesson = new string('a', limits.MaxLessonLength) + new string('\u200B', 50),
        }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(new string('a', limits.MaxLessonLength), result.Record!.Reflection!.Lesson);
    }

    [Fact]
    public async Task A_field_that_is_empty_after_neutralization_is_treated_as_absent()
    {
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with
        {
            SuccessfulApproaches = ["\u200B\u200D", .. reflection.SuccessfulApproaches],
            Warnings = ["", "   ", "\u202E\t", null!, "kept"],
            Preconditions = ["\uE000"],
            ReuseGuidance = "\u2066\u2069",
        }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
        Assert.Single(stored.SuccessfulApproaches);
        Assert.Equal(["kept"], stored.Warnings);
        Assert.Empty(stored.Preconditions);
        Assert.Null(stored.ReuseGuidance);
    }

    [Theory]
    [InlineData("\u200B\u202E")]
    [InlineData(" \t ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_lesson_that_is_absent_after_neutralization_quarantines_the_record(string? lesson)
    {
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Lesson = lesson! }));

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.MissingLesson);
        Assert.Contains("Lesson", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ListFieldNames))]
    public async Task A_missing_list_quarantines_the_record(string field)
    {
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => WithList(reflection, field, null!)));

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.MissingField);
        Assert.Contains(field + " is missing", result.Failure!.Reason, StringComparison.Ordinal);
    }

    public static TheoryData<string> ListFieldNames => [.. ListFields];

    // ---------------------------------------------------------------------------------------------
    // The host's sanitizer
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_host_sanitizer_sees_exactly_the_free_text_fields_as_an_ExperienceReflection_payload()
    {
        var sanitizer = new ScriptedSanitizer();
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        await harness.FinalizeAsync();

        var payload = Assert.Single(sanitizer.Payloads);
        Assert.Equal("ExperienceReflection", payload.Kind);
        Assert.Equal(ReflectionScreening.PayloadKind, payload.Kind);
        Assert.Equal(ReflectionScreening.ScreenedFieldNames.Order(StringComparer.Ordinal), payload.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.IsType<string>(payload.Fields[nameof(Reflection.Lesson)]);
        foreach (var list in ListFields)
        {
            Assert.IsAssignableFrom<IReadOnlyList<string>>(payload.Fields[list]);
        }
    }

    [Fact]
    public async Task A_host_sanitizer_that_rejects_quarantines_the_record_without_repeating_its_reason()
    {
        var sanitizer = new ScriptedSanitizer { Reject = true };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerRejected);
        Assert.Contains("sanitizer rejected", result.Failure!.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, result.Failure.Reason, StringComparison.Ordinal);
        Assert.Null(result.Failure.Exception);
    }

    [Fact]
    public async Task A_host_sanitizer_that_throws_quarantines_the_record_naming_only_the_type()
    {
        var sanitizer = new ScriptedSanitizer { Throw = new InvalidOperationException("saw " + Marker) };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerFailed);
        Assert.DoesNotContain(Marker, result.Failure!.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), result.Failure.Reason, StringComparison.Ordinal);

        // The exception is withheld -- its message may quote the reflection -- and only its type is kept.
        Assert.Null(result.Failure.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Failure.ExceptionType);
    }

    [Fact]
    public async Task A_host_sanitizer_that_cancels_on_its_own_quarantines_the_record()
    {
        var sanitizer = new ScriptedSanitizer { Throw = new OperationCanceledException() };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerFailed);
        Assert.Equal(typeof(OperationCanceledException).FullName, result.Failure!.ExceptionType);
    }

    [Fact]
    public async Task The_callers_cancellation_during_screening_still_propagates()
    {
        using var caller = new CancellationTokenSource();
        var sanitizer = new ScriptedSanitizer { OnCall = caller.Cancel, HangHonoringToken = true };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.FinalizeAsync(caller.Token));
        Assert.Null(harness.Store.Find(ExperienceFinalizationService.ExperienceIdFor(harness.RunId, ScreeningHarness.TestScope)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_sanitizer_that_does_not_answer_in_time_quarantines_the_record(bool honorsToken)
    {
        var options = new ExperienceFinalizationOptions
        {
            ReflectionLimits = ReflectionLimits.Default with { SanitizerTimeout = TimeSpan.FromMilliseconds(100) },
        };
        var sanitizer = new ScriptedSanitizer { HangHonoringToken = honorsToken, HangIgnoringToken = !honorsToken };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer, options: options);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerTimedOut);
        Assert.Null(result.Failure!.ExceptionType);
    }

    [Fact]
    public void The_sanitizer_timeout_defaults_to_five_seconds_and_must_be_positive()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ReflectionLimits.Default.SanitizerTimeout);
        Assert.Equal(ReflectionLimits.DefaultSanitizerTimeout, new ReflectionLimits().SanitizerTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReflectionLimits.Default with { SanitizerTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => ReflectionLimits.Default with { SanitizerTimeout = Timeout.InfiniteTimeSpan });
        Assert.Throws<ArgumentOutOfRangeException>(() => ReflectionLimits.Default with { SanitizerTimeout = TimeSpan.MaxValue });
    }

    [Fact]
    public async Task A_host_sanitizer_that_redacts_stores_the_redaction_and_reports_only_the_paths()
    {
        const string Secret = "sk-live-0123456789abcdef";
        var sanitizer = new ScriptedSanitizer
        {
            Redact = Secret,
            // A path that would leak the value is dropped, never reported.
            ExtraPaths = ["Lesson=" + Secret, "Unrelated", "Warnings[x]"],
        };
        var harness = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with
            {
                Lesson = reflection.Lesson + " Token used: " + Secret,
                Warnings = ["first", "leaked " + Secret, "third"],
            }),
            sanitizer: sanitizer);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
        Assert.EndsWith("Token used: [REDACTED]", stored.Lesson, StringComparison.Ordinal);
        Assert.Equal(["first", "leaked [REDACTED]", "third"], stored.Warnings);
        Assert.DoesNotContain(Secret, Json(stored), StringComparison.Ordinal);

        Assert.Equal([nameof(Reflection.Lesson), "Warnings[1]"], result.ReflectionRedactedFieldPaths);
        Assert.All(result.ReflectionRedactedFieldPaths, path => Assert.DoesNotContain(Secret, path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_policy_for_the_kind_redacts_through_the_default_sanitizer()
    {
        // The acceptance criterion: a secret-shaped lesson, and a host sanitizer policy that redacts it.
        var options = new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(ScreeningHarness.CaptureOptions.Policies, StringComparer.Ordinal)
        {
            [ReflectionScreening.PayloadKind] = ReflectionScreening.DefaultSanitizationPolicy with
            {
                SecretFieldNames = new HashSet<string>(StringComparer.Ordinal) { nameof(Reflection.Lesson) },
            },
        });
        var harness = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with { Lesson = "Use key sk-live-0123456789abcdef." }),
            sanitizer: new DefaultSanitizer(options, new FixedRedactor("[REDACTED]")));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal("[REDACTED]", harness.Store.Find(result.Record!.ExperienceId)!.Reflection!.Lesson);
        Assert.Equal([nameof(Reflection.Lesson)], result.ReflectionRedactedFieldPaths);
    }

    [Fact]
    public async Task A_whole_list_redacted_by_the_default_redactor_is_stored_empty()
    {
        var options = new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
        {
            [ReflectionScreening.PayloadKind] = ReflectionScreening.DefaultSanitizationPolicy with
            {
                SecretFieldNames = new HashSet<string>(StringComparer.Ordinal) { nameof(Reflection.Preconditions) },
            },
        });
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: new DefaultSanitizer(options));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Empty(result.Record!.Reflection!.Preconditions);
        Assert.Equal([nameof(Reflection.Preconditions)], result.ReflectionRedactedFieldPaths);
    }

    [Fact]
    public async Task What_a_host_sanitizer_returns_is_held_to_the_hygiene_layer_again()
    {
        var harness = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Lesson)] = "clean\u202E\u200B" });
        var over = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Lesson)] = new string('r', 4_001) });
        var shape = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Warnings)] = 42 });
        var omitted = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields.Remove(nameof(Reflection.Lesson)) });

        Assert.Equal("clean", (await harness.FinalizeAsync()).Record!.Reflection!.Lesson);
        AssertQuarantinedByScreening(await over.FinalizeAsync(), over, ReflectionScreeningRefusal.OverLimit);
        AssertQuarantinedByScreening(await shape.FinalizeAsync(), shape, ReflectionScreeningRefusal.SanitizerFailed);
        AssertQuarantinedByScreening(await omitted.FinalizeAsync(), omitted, ReflectionScreeningRefusal.FieldOmitted);
    }

    [Fact]
    public async Task Bound_fields_are_never_touched()
    {
        const string Producer = "host-reflector/\u202E1.0";
        var recorder = new RecordingReflector(new RewritingReflector(reflection => reflection with
        {
            Lesson = "\u202E" + reflection.Lesson,
            Producer = Producer,
        }));
        var harness = await ScreeningHarness.WithRunAsync(
            recorder,
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Lesson)] = "rewritten" });

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var produced = Assert.Single(recorder.Produced);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
        Assert.Equal("rewritten", stored.Lesson);
        Assert.Equal(produced.ReflectionId, stored.ReflectionId);
        Assert.Equal(produced.ExperienceRunId, stored.ExperienceRunId);
        Assert.Equal(produced.EvidenceIds, stored.EvidenceIds);
        Assert.Equal(produced.VerificationStatus, stored.VerificationStatus);
        Assert.Equal(produced.CompletionScore, stored.CompletionScore);
        Assert.Equal(produced.VerificationRuleVersion, stored.VerificationRuleVersion);
        Assert.Equal(produced.CreatedAt, stored.CreatedAt);

        // Screened like the free text, not bound: its invisible characters are removed.
        Assert.Equal("host-reflector/1.0", stored.Producer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\u200B\u202E")]
    public async Task A_producer_that_is_absent_after_neutralization_quarantines_the_record(string? producer)
    {
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Producer = producer! }));

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.MissingProducer);
    }

    [Fact]
    public async Task A_screening_failure_never_stores_a_Validated_record_and_reasons_carry_no_reflection_text()
    {
        var reasons = new List<string>();
        foreach (var rewrite in HostileRewrites())
        {
            var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(rewrite.Reflector), sanitizer: rewrite.Sanitizer);

            var result = await harness.FinalizeAsync();

            AssertQuarantinedByScreening(result, harness, rewrite.Expected);
            Assert.DoesNotContain(TypeMarker, result.Failure!.Reason, StringComparison.Ordinal);
            reasons.Add(result.Failure!.Reason);
            Assert.DoesNotContain(Marker, result.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, Json(harness.Store.Find(result.Record!.ExperienceId)!), StringComparison.Ordinal);
        }

        Assert.All(reasons, reason => Assert.DoesNotContain(Marker, reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_default_sanitizer_allows_the_kind_unchanged_without_a_configured_policy_and_still_rejects_other_unknown_kinds()
    {
        var sanitizer = new DefaultSanitizer(SanitizationOptions.Empty);
        var fields = new Dictionary<string, object?>
        {
            [nameof(Reflection.Lesson)] = "lesson",
            [nameof(Reflection.Warnings)] = new[] { "w1", "w2" },
            [nameof(Reflection.ReuseGuidance)] = null,
        };

        var allowed = await sanitizer.SanitizeAsync(new RawPayload(ReflectionScreening.PayloadKind, fields));
        var rejected = await sanitizer.SanitizeAsync(new RawPayload("SomethingElse", fields));

        Assert.Equal(SanitizationDecision.Allowed, allowed.Decision);
        Assert.Equal("lesson", allowed.Fields[nameof(Reflection.Lesson)]);
        Assert.Equal(new object?[] { "w1", "w2" }, Assert.IsAssignableFrom<IEnumerable<object?>>(allowed.Fields[nameof(Reflection.Warnings)]));
        Assert.Null(allowed.Fields[nameof(Reflection.ReuseGuidance)]);
        Assert.Empty(allowed.RedactedFieldPaths);
        Assert.Empty(allowed.OmittedFieldPaths);
        Assert.Equal(SanitizationDecision.Rejected, rejected.Decision);
    }

    [Fact]
    public void AddAgentExperienceCore_wires_the_registered_sanitizer_and_finalization_options()
    {
        var options = new ExperienceFinalizationOptions { ReflectionLimits = new ReflectionLimits(MaxLessonLength: 123) };
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(new LoopRecordStore());
        services.AddAgentExperienceCore(ScreeningHarness.CaptureOptions, new CaptureLimits(10, 10, 1_000, 1_000));
        services.AddSingleton(options);

        using var provider = services.BuildServiceProvider();

        Assert.Same(options, provider.GetRequiredService<ExperienceFinalizationService>().Options);
    }

    [Fact]
    public async Task A_host_sanitizer_registered_in_the_container_screens_reflections()
    {
        var sanitizer = new ScriptedSanitizer { Reject = true };
        var store = new LoopRecordStore();
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddSingleton<ISanitizer>(sanitizer);
        services.AddSingleton<IExperienceCaptureService>(new InMemoryExperienceCaptureService(
            new DefaultSanitizer(ScreeningHarness.CaptureOptions),
            new CaptureLimits(10, 10, 1_000, 1_000)));
        services.AddAgentExperienceCore(ScreeningHarness.CaptureOptions, new CaptureLimits(10, 10, 1_000, 1_000));

        using var provider = services.BuildServiceProvider();
        var harness = await ScreeningHarness.OverAsync(
            (InMemoryExperienceCaptureService)provider.GetRequiredService<IExperienceCaptureService>(),
            store,
            provider.GetRequiredService<ExperienceFinalizationService>(),
            "one-attempt");

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerRejected);
        Assert.Single(sanitizer.Payloads);
    }

    // ---------------------------------------------------------------------------------------------
    // Story 14.1 review follow-ups
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string> OversizedRunShapes => ["long-result", "many-erroring-tools", "many-warnings"];

    [Theory]
    [MemberData(nameof(OversizedRunShapes))]
    public async Task The_default_reflector_bounds_its_own_output_so_a_large_run_still_validates(string shape)
    {
        var recorder = new RecordingReflector(new DefaultExperienceReflector());
        var harness = await ScreeningHarness.WithRunAsync(recorder, shape);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var produced = Assert.Single(recorder.Produced);
        Assert.Equal(Json(produced), Json(harness.Store.Find(result.Record!.ExperienceId)!.Reflection!));

        var limits = ReflectionLimits.Default;
        Assert.InRange(produced.Lesson.Length, 1, limits.MaxLessonLength);
        Assert.InRange(produced.ReuseGuidance!.Length, 1, limits.MaxLessonLength);
        foreach (var list in new[] { produced.SuccessfulApproaches, produced.FailedApproaches, produced.Preconditions, produced.Warnings })
        {
            Assert.InRange(list.Count, 0, limits.MaxListItems);
            Assert.All(list, item => Assert.InRange(item.Length, 1, limits.MaxListItemLength));
            Assert.All(list, item => Assert.False(item.Length > 0 && char.IsHighSurrogate(item[^1])));
        }

        switch (shape)
        {
            case "long-result":
                Assert.Contains("…\"", Assert.Single(produced.SuccessfulApproaches), StringComparison.Ordinal);
                break;
            case "many-erroring-tools":
                Assert.EndsWith("…", Assert.Single(produced.FailedApproaches), StringComparison.Ordinal);
                break;
            default:
                Assert.Equal(limits.MaxListItems, produced.Warnings.Count);
                Assert.Matches(@"^\d+ further warnings are not listed\.$", produced.Warnings[^1]);
                break;
        }
    }

    [Fact]
    public async Task An_invisible_character_the_default_reflector_quotes_is_removed_from_the_stored_reflection()
    {
        var recorder = new RecordingReflector(new DefaultExperienceReflector());
        var harness = await ScreeningHarness.WithRunAsync(recorder, "invisible-error");

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var produced = Assert.Single(recorder.Produced);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!.Reflection!;
        Assert.Contains(produced.FailedApproaches, item => item.Contains("dis\u200Bk\tfull", StringComparison.Ordinal));
        Assert.Equal(produced.FailedApproaches.Select(item => item.Replace("\u200B", string.Empty, StringComparison.Ordinal).Replace('\t', ' ')), stored.FailedApproaches);
        Assert.Contains(stored.FailedApproaches, item => item.Contains("disk full", StringComparison.Ordinal));
        Assert.Equal(Json(produced with { FailedApproaches = stored.FailedApproaches }), Json(stored));
    }

    [Fact]
    public async Task Blank_rendering_ignorables_line_separators_and_long_mark_runs_are_neutralized()
    {
        const string Dirty = "a\uFE0Fb\U000E0100c\u034Fd\u115Fe\u1160f\u3164g\uFFA0h\u2800i\u2028j\u2029k" + "e\u0301\u0301\u0301\u0301\u0301\u0301" + "|\u0301\u200B\u0301\u0301\u0301\u0301";
        const string Clean = "abcdefghi j k" + "e\u0301\u0301\u0301\u0301" + "|\u0301\u0301\u0301\u0301";
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Lesson = Dirty, Warnings = [Dirty] }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(Clean, result.Record!.Reflection!.Lesson);
        Assert.Equal([Clean], result.Record.Reflection.Warnings);
    }

    [Fact]
    public async Task Lone_and_reversed_surrogates_are_removed()
    {
        // Inline, not [InlineData]: a test framework may re-encode an ill-formed string it serializes.
        (string Dirty, string Clean)[] cases =
        [
            ("a\uDC00\uD800b", "ab"),
            ("a\uD800\uD800\uDC00b", "a\U00010000b"),
            ("\uDFFFa\uDBFF", "a"),
        ];

        foreach (var (dirty, clean) in cases)
        {
            var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Lesson = dirty }));

            var result = await harness.FinalizeAsync();

            Assert.Equal(clean, result.Record!.Reflection!.Lesson);
        }
    }

    [Theory]
    [InlineData(3_998, FinalizationOutcome.Validated)]
    [InlineData(3_999, FinalizationOutcome.Quarantined)]
    public async Task Lengths_are_counted_in_UTF16_code_units(int prefix, FinalizationOutcome expected)
    {
        // One astral character is two UTF-16 code units: 3,998 + 2 is exactly the limit, 3,999 + 2 is over it.
        var lesson = new string('a', prefix) + "\U0001F600";
        var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Lesson = lesson }));

        var result = await harness.FinalizeAsync();

        Assert.Equal(expected, result.Outcome);
        if (expected == FinalizationOutcome.Validated)
        {
            Assert.Equal(lesson, result.Record!.Reflection!.Lesson);
        }
        else
        {
            AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.OverLimit);
        }
    }

    [Fact]
    public async Task Over_limit_reasons_name_the_reflectors_own_index_before_and_after_the_sanitizer()
    {
        var item = new string('i', ReflectionLimits.Default.MaxListItemLength + 1);
        var before = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflection => reflection with { Warnings = ["", "\u200B", "ok", item] }));
        var after = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with { Warnings = ["", "a", "ITEM-B"] }),
            sanitizer: new ScriptedSanitizer { Redact = "ITEM-B", Replacement = item });

        var refusedBefore = await before.FinalizeAsync();
        var refusedAfter = await after.FinalizeAsync();

        AssertQuarantinedByScreening(refusedBefore, before, ReflectionScreeningRefusal.OverLimit);
        Assert.Contains("Warnings[3] is longer", refusedBefore.Failure!.Reason, StringComparison.Ordinal);

        // The sanitizer saw ["a", "b"] and grew its item 1, which is the reflector's item 2.
        AssertQuarantinedByScreening(refusedAfter, after, ReflectionScreeningRefusal.OverLimit);
        Assert.Contains("Warnings[2] is longer", refusedAfter.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redacted_paths_index_into_the_stored_lists()
    {
        const string Secret = "sk-live-0123456789abcdef";
        var harness = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with { Warnings = ["\u200B", Secret, "keep", "tail " + Secret] }),
            sanitizer: new ScriptedSanitizer { Redact = Secret, Replacement = string.Empty });

        var result = await harness.FinalizeAsync();

        // The sanitizer saw [secret, "keep", "tail secret"] and redacted items 0 and 2. Item 0 was then blank
        // and dropped, so it names nothing stored; item 2 is stored at index 1.
        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(["keep", "tail "], result.Record!.Reflection!.Warnings);
        Assert.Equal(["Warnings[1]"], result.ReflectionRedactedFieldPaths);
    }

    [Fact]
    public async Task A_sanitizer_that_grows_a_list_past_the_item_limit_quarantines_the_record()
    {
        var harness = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Warnings)] = Enumerable.Range(0, 33).Select(i => $"w{i}").ToList() });

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.OverLimit);
    }

    [Fact]
    public async Task A_redaction_that_grows_a_list_item_past_its_limit_quarantines_the_record()
    {
        var harness = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(reflection => reflection with { Warnings = ["SECRET"] }),
            sanitizer: new ScriptedSanitizer { Redact = "SECRET", Replacement = new string('*', 1_001) });

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.OverLimit);
        Assert.Contains("Warnings[0]", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_returned_as_a_single_string_is_stored_as_one_item_and_unknown_keys_are_ignored()
    {
        var harness = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer
            {
                ExtraPaths = ["Warnings[0]"],
                Rewrite = fields =>
                {
                    fields[nameof(Reflection.Warnings)] = "[warnings redacted]";
                    fields["Injected"] = ReflectionScreeningTests.Marker;
                },
            });

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(["[warnings redacted]"], result.Record!.Reflection!.Warnings);
        Assert.DoesNotContain(Marker, Json(result.Record), StringComparison.Ordinal);

        // An item path into a list that came back whole names nothing stored.
        Assert.Empty(result.ReflectionRedactedFieldPaths);
    }

    public static TheoryData<string> OmissionCases => ["reported", "missing-lesson", "missing-list", "null-list", "null-item", "null-guidance"];

    [Theory]
    [MemberData(nameof(OmissionCases))]
    public async Task A_sanitizer_that_omits_a_field_or_an_item_quarantines_the_record(string omission)
    {
        var sanitizer = new ScriptedSanitizer
        {
            OmittedPaths = omission == "reported" ? ["Warnings[0]"] : [],
            Rewrite = fields =>
            {
                switch (omission)
                {
                    case "missing-lesson": fields.Remove(nameof(Reflection.Lesson)); break;
                    case "missing-list": fields.Remove(nameof(Reflection.Preconditions)); break;
                    case "null-list": fields[nameof(Reflection.Warnings)] = null; break;
                    case "null-item": fields[nameof(Reflection.Warnings)] = new List<object?> { null }; break;
                    case "null-guidance": fields[nameof(Reflection.ReuseGuidance)] = null; break;
                }
            },
        };
        var harness = await ScreeningHarness.WithRunAsync(new DefaultExperienceReflector(), sanitizer: sanitizer);

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.FieldOmitted);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(42)]
    public async Task A_decision_that_is_neither_Allowed_nor_Rejected_quarantines_the_record(int decision)
    {
        var harness = await ScreeningHarness.WithRunAsync(
            new DefaultExperienceReflector(),
            sanitizer: new ScriptedSanitizer { Decision = (SanitizationDecision)decision });

        var result = await harness.FinalizeAsync();

        AssertQuarantinedByScreening(result, harness, ReflectionScreeningRefusal.SanitizerFailed);
    }

    [Fact]
    public void The_built_in_policy_is_bounded_by_the_limits()
    {
        var policy = ReflectionScreening.DefaultSanitizationPolicy;
        Assert.Equal(ReflectionLimits.Default.MaxLessonLength, policy.MaxValueLength);
        Assert.Equal(ReflectionLimits.Default.MaxListItems, policy.MaxFieldCount);
        Assert.Equal(2, policy.MaxDepth);
        Assert.Empty(policy.SecretFieldNames);
        Assert.Equal(ReflectionScreening.ScreenedFieldNames.Order(StringComparer.Ordinal), policy.AllowedFieldNames.Order(StringComparer.Ordinal));

        var raised = ReflectionScreening.SanitizationPolicyFor(new ReflectionLimits(MaxLessonLength: 100, MaxListItemLength: 9_000, MaxListItems: 64));
        Assert.Equal(9_000, raised.MaxValueLength);
        Assert.Equal(64, raised.MaxFieldCount);
    }

    [Fact]
    public async Task A_host_default_sanitizer_without_a_policy_rejects_what_raised_limits_allow()
    {
        // The pitfall the docs name: raising the limits and screening through a DefaultSanitizer that uses the
        // built-in (default-bounded) policy.
        var options = new ExperienceFinalizationOptions { ReflectionLimits = ReflectionLimits.Default with { MaxListItems = 40 } };
        var rewrite = new RewritingReflector(reflection => reflection with { Warnings = [.. Enumerable.Range(0, 40).Select(i => $"w{i}")] });
        var builtIn = await ScreeningHarness.WithRunAsync(rewrite, sanitizer: new DefaultSanitizer(ScreeningHarness.CaptureOptions), options: options);
        var configured = await ScreeningHarness.WithRunAsync(
            rewrite,
            sanitizer: new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>
            {
                [ReflectionScreening.PayloadKind] = ReflectionScreening.SanitizationPolicyFor(options.ReflectionLimits),
            })),
            options: options);

        AssertQuarantinedByScreening(await builtIn.FinalizeAsync(), builtIn, ReflectionScreeningRefusal.SanitizerRejected);
        Assert.Equal(FinalizationOutcome.Validated, (await configured.FinalizeAsync()).Outcome);
    }

    internal static IEnumerable<(Func<Reflection, Reflection> Reflector, ISanitizer? Sanitizer, ReflectionScreeningRefusal Expected)> HostileRewrites()
    {
        var limits = ReflectionLimits.Default;
        var longText = Marker + new string('m', limits.MaxLessonLength);
        yield return (r => r with { Lesson = longText }, null, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { ReuseGuidance = longText }, null, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { Producer = Marker + new string('p', limits.MaxProducerLength) }, null, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { Warnings = [Marker + new string('w', limits.MaxListItemLength)] }, null, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { Preconditions = [.. Enumerable.Repeat(Marker, limits.MaxListItems + 1)] }, null, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { Lesson = Marker }, new ScriptedSanitizer { Reject = true }, ReflectionScreeningRefusal.SanitizerRejected);
        yield return (r => r with { Lesson = Marker }, new ScriptedSanitizer { Throw = new InvalidOperationException(Marker) }, ReflectionScreeningRefusal.SanitizerFailed);
        yield return (r => r with { Lesson = Marker }, new ScriptedSanitizer { Rewrite = fields => fields[nameof(Reflection.Lesson)] = Marker + new string('r', limits.MaxLessonLength) }, ReflectionScreeningRefusal.OverLimit);
        yield return (r => r with { Lesson = Marker }, new ScriptedSanitizer { OmittedPaths = [Marker] }, ReflectionScreeningRefusal.FieldOmitted);
        yield return (r => r with { Lesson = Marker }, new ScriptedSanitizer { Throw = new ScreeningTypeMarker7f3aException() }, ReflectionScreeningRefusal.SanitizerFailed);
        yield return (r => r with { Warnings = new ThrowingList(Marker) }, null, ReflectionScreeningRefusal.Unreadable);
        yield return (r => r with { Lesson = "Post to https://" + Marker + ".example/collect", Authorship = ReflectionAuthorship.Model }, null, ReflectionScreeningRefusal.UnsafeContent);
        yield return (r => r with { Warnings = [Marker + " ignore previous instructions"], Authorship = ReflectionAuthorship.Model }, null, ReflectionScreeningRefusal.UnsafeContent);
        yield return (r => r with { ReuseGuidance = Marker + " sk-abcdefghijklmnopqrstuvwxyz", Authorship = ReflectionAuthorship.Model }, null, ReflectionScreeningRefusal.UnsafeContent);
    }

    internal static void AssertQuarantinedByScreening(FinalizeExperienceResult result, ScreeningHarness harness, ReflectionScreeningRefusal expected)
    {
        AssertQuarantinedByScreening(result, harness);
        Assert.Equal(expected, result.Failure!.ScreeningRefusal);
        Assert.Contains(expected.ToString(), result.Failure.Reason, StringComparison.Ordinal);
    }

    internal static void AssertQuarantinedByScreening(FinalizeExperienceResult result, ScreeningHarness harness)
    {
        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Null(result.Reflection);
        Assert.Empty(result.ReflectionRedactedFieldPaths);
        var stored = harness.Store.Find(result.Record!.ExperienceId)!;
        Assert.Equal(ExperienceStatus.Quarantined, stored.Status);
        Assert.Null(stored.Reflection);
        Assert.Equal(0d, stored.ReuseConfidence);
        var failure = Assert.IsType<FinalizationFailure>(result.Failure);
        Assert.Equal(FinalizationStage.Reflect, failure.Stage);
        Assert.NotNull(failure.ScreeningRefusal);
        Assert.Null(failure.Exception);
    }

    private static Reflection Violate(Reflection reflection, string violation, int textLength, int itemLength, int items, int producerLength)
    {
        var text = new string('t', textLength);
        var item = new string('i', itemLength);
        return violation switch
        {
            "lesson" => reflection with { Lesson = text },
            "guidance" => reflection with { ReuseGuidance = text },
            "producer" => reflection with { Producer = new string('p', producerLength) },
            _ when violation.StartsWith("item:", StringComparison.Ordinal) => WithList(reflection, violation["item:".Length..], [item]),
            _ when violation.StartsWith("count:", StringComparison.Ordinal) => WithList(reflection, violation["count:".Length..], [.. Enumerable.Range(0, items).Select(i => $"item {i}")]),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, null),
        };
    }

    private static Reflection WithList(Reflection reflection, string field, IReadOnlyList<string> list) => field switch
    {
        nameof(Reflection.SuccessfulApproaches) => reflection with { SuccessfulApproaches = list },
        nameof(Reflection.FailedApproaches) => reflection with { FailedApproaches = list },
        nameof(Reflection.Preconditions) => reflection with { Preconditions = list },
        nameof(Reflection.Warnings) => reflection with { Warnings = list },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    internal static string Json<T>(T value) => JsonSerializer.Serialize(value);
}

/// <summary>
/// No reflection text reaches telemetry: the spans and measurements a host would export while
/// finalization refuses, redacts or neutralizes a reflection carrying a marker hold no trace of it.
/// </summary>
[Collection(TelemetryCollection.Name)]
public class ReflectionScreeningTelemetryTests
{
    [Fact]
    public async Task No_reflection_text_reaches_spans_or_measurements()
    {
        using var probe = TelemetryProbe.All();

        var codes = new List<string?>();
        foreach (var (reflector, sanitizer, expected) in ReflectionScreeningTests.HostileRewrites())
        {
            var harness = await ScreeningHarness.WithRunAsync(new RewritingReflector(reflector), sanitizer: sanitizer);
            await harness.FinalizeAsync();
            codes.Add(expected.ToString());
        }

        // The refusal is on the finalize span, as a closed-set member name, and only on a screening refusal.
        var finalizeSpans = probe.LibraryActivities.Where(activity => activity.OperationName == "agentexperience.finalize").ToList();
        Assert.Equal(codes, finalizeSpans.Select(span => span.GetTagItem("agentexperience.reflection.screening_refusal") as string));

        var redacting = await ScreeningHarness.WithRunAsync(
            new RewritingReflector(r => r with { Lesson = r.Lesson + " " + ReflectionScreeningTests.Marker }),
            sanitizer: new ScriptedSanitizer { Redact = ReflectionScreeningTests.Marker });
        Assert.Equal(FinalizationOutcome.Validated, (await redacting.FinalizeAsync()).Outcome);

        Assert.NotEmpty(probe.LibraryActivities);
        foreach (var activity in probe.Activities)
        {
            Assert.DoesNotContain(ReflectionScreeningTests.Marker, activity.DisplayName, StringComparison.Ordinal);
            Assert.DoesNotContain(ReflectionScreeningTests.Marker, activity.StatusDescription ?? string.Empty, StringComparison.Ordinal);
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain(ReflectionScreeningTests.Marker, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(ReflectionScreeningTests.Marker, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);
            }

            foreach (var activityEvent in activity.Events)
            {
                Assert.DoesNotContain(ReflectionScreeningTests.Marker, activityEvent.Name, StringComparison.Ordinal);
                foreach (var tag in activityEvent.Tags)
                {
                    Assert.DoesNotContain(ReflectionScreeningTests.Marker, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }

        foreach (var measurement in probe.Measurements)
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.DoesNotContain(ReflectionScreeningTests.Marker, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }
}

/// <summary>One captured, completed run and a finalization service over it.</summary>
internal sealed class ScreeningHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    internal static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    internal static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);
    internal static readonly ClosedVerificationRound Round = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "rev-1");

    internal static readonly SanitizationOptions CaptureOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    private ScreeningHarness(InMemoryExperienceCaptureService capture, LoopRecordStore store, ExperienceFinalizationService service)
    {
        Capture = capture;
        Store = store;
        Service = service;
    }

    internal InMemoryExperienceCaptureService Capture { get; }

    internal LoopRecordStore Store { get; }

    internal ExperienceFinalizationService Service { get; }

    internal Guid RunId { get; private set; }

    internal static Task<ScreeningHarness> WithRunAsync(
        IExperienceReflector reflector,
        string shape = "one-attempt",
        ISanitizer? sanitizer = null,
        ExperienceFinalizationOptions? options = null)
    {
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(CaptureOptions), new CaptureLimits(50, 50, 10_000, 10_000));
        var store = new LoopRecordStore();
        var service = new ExperienceFinalizationService(
            capture,
            reflector,
            store,
            new ExperienceLifecycleService(store),
            indexingService: null,
            indexingTimeout: null,
            provenanceSigning: null,
            reflectionSanitizer: sanitizer,
            options: options);
        return OverAsync(capture, store, service, shape);
    }

    internal static async Task<ScreeningHarness> OverAsync(
        InMemoryExperienceCaptureService capture,
        LoopRecordStore store,
        ExperienceFinalizationService service,
        string shape)
    {
        var harness = new ScreeningHarness(capture, store, service) { RunId = Guid.NewGuid() };

        var metadata = shape == "unknown-preconditions"
            ? new Dictionary<string, string> { ["region"] = " ", ["tier"] = "gold" }
            : new Dictionary<string, string> { ["region"] = "eu-west" };
        var environment = shape == "unknown-preconditions"
            ? new EnvironmentFingerprint("host-1", null!, "", null, metadata)
            : new EnvironmentFingerprint("host-1", "net10.0", "test-os", "2.1.0", metadata);

        var started = capture.StartRun(
            harness.RunId,
            taskId: "task-1",
            taskDescription: "a test task",
            scope: TestScope,
            environment: environment,
            provenance: new Provenance("unit-tests", "1.0.0", Now, null),
            startedAt: Now);
        Assert.Equal(StartRunOutcome.Started, started.Outcome);

        foreach (var attempt in Attempts(shape))
        {
            var appended = await capture.AppendAttemptAsync(harness.RunId, attempt);
            Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);
        }

        var completed = await capture.CompleteRunAsync(harness.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1));
        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        return harness;
    }

    internal Task<FinalizeExperienceResult> FinalizeAsync(CancellationToken cancellationToken = default) => Service.FinalizeAsync(new FinalizeExperienceRequest(
        RunId: RunId,
        Authorization: Authorization,
        ClosedRound: Round,
        RequiredChecks: [new RequiredCheck("tests", "TestResult")],
        Evidence:
        [
            new Evidence(Guid.NewGuid(), Round.RoundId, Round.ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, Now),
        ],
        CurrentArtifactRevision: Round.ArtifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: Now.AddMinutes(2)),
        cancellationToken);

    private static IEnumerable<AppendAttemptRequest> Attempts(string shape)
    {
        RawToolCall Call(string name, string? result, string? error) =>
            new(Guid.NewGuid(), name, new Dictionary<string, object?> { ["query"] = "q" }, Now, TimeSpan.FromMilliseconds(5), result, error);

        switch (shape)
        {
            case "failed-then-succeeded":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [Call("lookup", null, "timeout")], null, "the lookup timed out");
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now.AddSeconds(2), TimeSpan.FromSeconds(1), [Call("lookup", "ok", null)], "done", null);
                break;

            case "quoted-errors":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [Call("write", null, "café said \"no\" at C:\\tmp\r\nline 2")], null, "Ünïcödé \"quote\" \\ back\nslash");
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now.AddSeconds(2), TimeSpan.FromSeconds(1), [], "résultat 👍", null);
                break;

            case "long-result":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [Call("lookup", new string('r', 4_000), null)], new string('x', 3_990) + "😀" + "tail", null);
                break;

            case "many-erroring-tools":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [.. Enumerable.Range(0, 50).Select(i => Call($"tool-{i}", null, new string('e', 600)))], null, new string('f', 4_000));
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now.AddSeconds(2), TimeSpan.FromSeconds(1), [], "done", null);
                break;

            case "many-warnings":
                foreach (var attempt in Enumerable.Range(0, 45))
                {
                    yield return new AppendAttemptRequest(Guid.NewGuid(), Now.AddSeconds(attempt), TimeSpan.FromSeconds(1), [], "partial", null);
                }

                break;

            case "invisible-error":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], null, "dis\u200Bk\tfull");
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now.AddSeconds(2), TimeSpan.FromSeconds(1), [], "done", null);
                break;

            case "links-in-run":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [Call("lookup", "see notevil.com, evil.com.au and github.com", null)], "fetched https://good.example/abc from 10.1.2.3", null);
                break;

            case "url-in-run":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [Call("fetch_docs", null, null)], "Fetched HTTPS://Docs.Example/Guide and mirror.docs.example", null);
                break;

            case "many-tools":
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [.. Enumerable.Range(0, 12).Select(i => Call($"tool-{i}", "r", i % 3 == 0 ? "e" : null))], "done", null);
                break;

            default:
                yield return new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null);
                break;
        }
    }
}

/// <summary>Records every reflection the inner reflector produced, exactly as it produced it.</summary>
internal sealed class RecordingReflector(IExperienceReflector inner) : IExperienceReflector
{
    public List<Reflection> Produced { get; } = [];

    public async Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default)
    {
        var reflection = await inner.ReflectAsync(request, cancellationToken);
        Produced.Add(reflection);
        return reflection;
    }
}

/// <summary>A host reflector that rewrites the default reflection's free text, leaving it bound.</summary>
internal sealed class RewritingReflector(Func<Reflection, Reflection> rewrite) : IExperienceReflector
{
    private readonly DefaultExperienceReflector _inner = new();

    public async Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default) =>
        rewrite(await _inner.ReflectAsync(request, cancellationToken));
}

/// <summary>A host sanitizer that records its payloads and rejects, throws, redacts or rewrites as scripted.</summary>
internal sealed class ScriptedSanitizer : ISanitizer
{
    public List<RawPayload> Payloads { get; } = [];

    public bool Reject { get; init; }

    public Exception? Throw { get; init; }

    /// <summary>Replaced by <c>[REDACTED]</c> wherever it appears, with the field's path reported.</summary>
    public string? Redact { get; init; }

    public IReadOnlyList<string> ExtraPaths { get; init; } = [];

    public Action<Dictionary<string, object?>>? Rewrite { get; init; }

    /// <summary>What <see cref="Redact"/> is replaced by.</summary>
    public string Replacement { get; init; } = "[REDACTED]";

    public IReadOnlyList<string> OmittedPaths { get; init; } = [];

    public SanitizationDecision Decision { get; init; } = SanitizationDecision.Allowed;

    public Action? OnCall { get; init; }

    /// <summary>Never answers, until the token it was handed is cancelled.</summary>
    public bool HangHonoringToken { get; init; }

    /// <summary>Never answers at all.</summary>
    public bool HangIgnoringToken { get; init; }

    public async Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
    {
        Payloads.Add(payload);
        OnCall?.Invoke();

        if (HangHonoringToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (HangIgnoringToken)
        {
            await new TaskCompletionSource().Task;
        }

        return Answer(payload);
    }

    private SanitizedPayload Answer(RawPayload payload)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        if (Reject)
        {
            return new SanitizedPayload(SanitizationDecision.Rejected, new Dictionary<string, object?>(), [], [], "rejected because of " + ReflectionScreeningTests.Marker);
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var redacted = new List<string>();
        foreach (var (key, value) in payload.Fields)
        {
            fields[key] = value switch
            {
                string text => RedactText(text, key, redacted),
                IEnumerable<string> items => items.Select((item, index) => RedactText(item, $"{key}[{index}]", redacted)).ToList(),
                _ => value,
            };
        }

        Rewrite?.Invoke(fields);
        return new SanitizedPayload(Decision, fields, [.. redacted, .. ExtraPaths], OmittedPaths, null);
    }

    private string RedactText(string text, string path, List<string> redacted)
    {
        if (Redact is null || !text.Contains(Redact, StringComparison.Ordinal))
        {
            return text;
        }

        redacted.Add(path);
        return text.Replace(Redact, Replacement, StringComparison.Ordinal);
    }
}

/// <summary>An exception whose type name carries <see cref="ReflectionScreeningTests.TypeMarker"/>.</summary>
internal sealed class ScreeningTypeMarker7f3aException : Exception;

/// <summary>A reflector list that throws, with a message carrying reflection text, when it is read.</summary>
internal sealed class ThrowingList(string text) : IReadOnlyList<string>
{
    public int Count => throw new InvalidOperationException(text);

    public string this[int index] => throw new InvalidOperationException(text);

    public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException(text);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A <see cref="Redactor"/> that replaces any value with a fixed marker.</summary>
internal sealed class FixedRedactor(string replacement) : Redactor
{
    public override int GetRedactedLength(ReadOnlySpan<char> input) => replacement.Length;

    public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
    {
        replacement.AsSpan().CopyTo(destination);
        return replacement.Length;
    }
}
