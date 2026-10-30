using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>A lessons-html case: what it shows, and the page.</summary>
internal sealed record LessonsHtmlCase(string Description, Func<ExperienceLessonsPage> Page);

/// <summary>
/// Contract 1.14's <c>lessons-html</c> family: the HTML document <see cref="ExperienceLessonsHtml.Render"/> writes for a
/// lessons page. Each case writes <c>page.json</c> (the page in the lessons-view <c>expected.json</c> form, with its
/// <c>scope</c> and a <c>description</c>) and <c>expected.html</c>, the document byte for byte.
/// </summary>
internal static class LessonsHtmlContract
{
    public const string Family = "lessons-html";

    private static readonly DateTimeOffset Now = Fixed.T0.AddDays(1).AddTicks(9_999_999);

    /// <summary>Every lessons-html case, by directory name.</summary>
    public static IReadOnlyList<(string Name, LessonsHtmlCase Case)> Cases { get; } =
    [
        ("listed-lessons", new(
            "The page a real .NET lessons view listed over the in-memory store (every lesson, eligible or not), one lesson injected once and one three times: each lesson's task, status and eligibility badges, confidence, instants (whole seconds, UTC), last injection, experience ID, checks table (none for a lesson with no evidence) and block. The clock's fraction of a second is cut, never rounded.",
            ListedLessons)),
        ("escaping", new(
            "Every value drawn from a record or the scope is HTML-encoded: <, >, &, the double quote and the apostrophe (as &#39;), every character from U+00A0 to U+00FF as a decimal reference, and a character outside the Basic Multilingual Plane as one decimal reference to its code point. Other characters, and the ineligibility reason, are written as they are once encoded. Check results Pass, Fail and Unknown take their classes.",
            Escaping)),
        ("no-lessons", new(
            "A page that was found but lists nothing, after leaving out two records the store answered from another scope.",
            () => new ExperienceLessonsPage(Fixed.Scope, ExperienceStoreOutcome.Found, [], Now) { InjectionsTracked = true, OutOfScopeDropped = 2 })),
        ("denied", new(
            "A page the store refused (the scope lies outside the authorization): it says what the store answered and lists nothing.",
            () => new ExperienceLessonsPage(Fixed.Scope with { TeamId = "payments", UserId = "user-17" }, ExperienceStoreOutcome.Denied, [], Now))),
        ("has-more-not-tracked", new(
            "One lesson of more (the page says so), from a store that does not track injections: its last injection is 'not tracked', its confidence of 0.125 is written 0.13 (half away from zero) and it has no checks.",
            HasMoreNotTracked)),
    ];

    private static ExperienceLessonsPage ListedLessons()
    {
        var clock = new FixedTimeProvider(Now);
        var store = new InMemoryExperienceRecordStore(clock);
        var records = new List<ExperienceRecord>
        {
            InjectOptionsContract.Retried(1, 0x150),
            InjectOptionsContract.Lesson(2, "rotate-keys", "A model wrote this: announce the rotation before starting.", [["announce"]], modelAuthored: true, family: 0x150),
            SearchContract.Seed(0x150, 3, "rotate-keys", "Rotate the signing keys", "Lock timeout.", ExperienceStatus.Quarantined, 0),
            SearchContract.Seed(0x150, 4, "rotate-keys", "Rotate the signing keys", "Below the floor.", confidence: 0.4) with { SupportingValidations = 1, Contradictions = 2 },
        };
        records[1] = records[1] with { CreatedAt = records[1].CreatedAt.AddDays(1), UpdatedAt = records[1].UpdatedAt.AddDays(2).AddTicks(9_999_999) };
        foreach (var record in records)
        {
            var created = store.CreateAsync(Fixed.Authorization, record, CancellationToken.None).GetAwaiter().GetResult();
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException($"{record.ExperienceId} was not stored: {created.Outcome}.");
            }
        }

        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions { ResolveRequest = _ => null, TimeProvider = clock });
        var page = new AgentExperienceLessons(store, provider, clock)
            .ListAsync(new ExperienceIdentity(Fixed.Authorization, Fixed.Scope))
            .GetAwaiter()
            .GetResult();

        // The in-memory store tracks injections; two lessons were shown to an agent.
        var lessons = page.Lessons.ToList();
        lessons[0] = lessons[0] with { LastInjectedAt = Fixed.T0.AddHours(3).AddTicks(5_000_000), InjectionCount = 1 };
        lessons[1] = lessons[1] with { LastInjectedAt = Fixed.T0.AddHours(-30), InjectionCount = 3 };
        return page with { Lessons = lessons };
    }

    private static ExperienceLessonsPage Escaping()
    {
        const string Hostile = "<script>alert('x')</script> & \"quoted\" é ÿ \u00A0 Ā 雪 😀 \u0085\t";
        var scope = new Scope("tenant <&>", "app \"quoted\"", "project 'single'", "team é", "agent 😀", "user ÿ");
        var lesson = new ExperienceLesson(
            Fixed.Id(0x151, 1),
            "task " + Hostile,
            ExperienceStatus.Validated,
            0.875,
            Fixed.T0,
            Fixed.T0.AddMinutes(1),
            [
                new ExperienceLessonCheck("check <1>", "kind & \"kind\"", CheckResult.Pass),
                new ExperienceLessonCheck("check 'two'", "smoke-test", CheckResult.Fail),
                new ExperienceLessonCheck("check é", "manual-review", CheckResult.Unknown),
            ],
            "--- BEGIN HISTORICAL REFERENCE ---\nLesson: " + Hostile + "\n--- END HISTORICAL REFERENCE ---\n",
            false,
            "status <Stale> & \"never\" retrieved",
            ExperienceLessonIneligibility.Status)
        {
            LastInjectedAt = Fixed.T0.AddSeconds(59.9999999),
            InjectionCount = 2,
        };
        return new ExperienceLessonsPage(scope, ExperienceStoreOutcome.Found, [lesson], Now) { InjectionsTracked = true };
    }

    private static ExperienceLessonsPage HasMoreNotTracked()
    {
        var lesson = new ExperienceLesson(
            Fixed.Id(0x152, 1),
            "rotate-keys",
            ExperienceStatus.Reinforced,
            0.125,
            Fixed.T0,
            Fixed.T0,
            [],
            "Lesson: Rotate the keys.\n",
            true,
            null);
        return new ExperienceLessonsPage(Fixed.Scope, ExperienceStoreOutcome.Found, [lesson], Now) { HasMore = true, InjectionsTracked = false };
    }

    /// <summary><c>page.json</c>: the description, the scope, and the page as the lessons view's <c>expected.json</c> holds it.</summary>
    public static JsonObject PageJson(LessonsHtmlCase definition)
    {
        var page = definition.Page();
        var json = new JsonObject
        {
            ["description"] = definition.Description,
            ["scope"] = JsonSerializer.SerializeToNode(page.Scope, ContractJson.Options),
        };
        foreach (var (key, value) in LessonsViewContract.PageJson(page))
        {
            json[key] = value?.DeepClone();
        }

        return json;
    }

    /// <summary>The page <c>page.json</c> holds, read back as a port reads it.</summary>
    public static ExperienceLessonsPage ReadPage(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var lessons = root["lessons"]!.AsArray().Select(node =>
        {
            var lesson = node!.AsObject();
            return new ExperienceLesson(
                Guid.Parse(lesson["experienceId"]!.GetValue<string>()),
                lesson["taskId"]!.GetValue<string>(),
                Enum.Parse<ExperienceStatus>(lesson["status"]!.GetValue<string>()),
                lesson["confidence"]!.GetValue<double>(),
                DateTimeOffset.Parse(lesson["createdAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(lesson["updatedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                [.. lesson["checks"]!.AsArray().Select(check => new ExperienceLessonCheck(
                    check!["checkId"]!.GetValue<string>(),
                    check["evidenceKind"]!.GetValue<string>(),
                    Enum.Parse<CheckResult>(check["result"]!.GetValue<string>())))],
                lesson["blockText"]!.GetValue<string>(),
                lesson["isEligible"]!.GetValue<bool>(),
                lesson["ineligibleReason"]?.GetValue<string>(),
                lesson["ineligibility"] is { } code ? Enum.Parse<ExperienceLessonIneligibility>(code.GetValue<string>()) : null)
            {
                LastInjectedAt = lesson["lastInjectedAt"] is { } at
                    ? DateTimeOffset.Parse(at.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                InjectionCount = lesson["injectionCount"]!.GetValue<long>(),
            };
        }).ToList();

        return new ExperienceLessonsPage(
            root["scope"].Deserialize<Scope>(ContractJson.Options)!,
            Enum.Parse<ExperienceStoreOutcome>(root["outcome"]!.GetValue<string>()),
            lessons,
            DateTimeOffset.Parse(root["generatedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture))
        {
            HasMore = root["hasMore"]!.GetValue<bool>(),
            OutOfScopeDropped = root["outOfScopeDropped"]!.GetValue<int>(),
            InjectionsTracked = root["injectionsTracked"]!.GetValue<bool>(),
        };
    }
}
