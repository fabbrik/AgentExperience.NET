using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// <see cref="ExperienceTaskText"/>: one test per row of its I/O matrix, plus the two context properties that
/// expose it. No model and no store: the rule is deterministic over the messages alone.
/// </summary>
public class ExperienceTaskTextTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage FromHistory(ChatMessage message) =>
        message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory, "history");

    private static ChatMessage FromProvider(ChatMessage message) =>
        message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "provider");

    // ---- Matrix ----------------------------------------------------------------------------------

    [Fact]
    public void A_single_prompt_is_the_task_text()
    {
        Assert.Equal(
            "Refund ticket 42 is stuck in pending",
            ExperienceTaskText.Derive([User("Refund ticket 42 is stuck in pending")]));
    }

    [Fact]
    public void A_short_follow_up_is_preceded_by_the_previous_user_message()
    {
        Assert.Equal(
            "Deploy service X to prod — and retry",
            ExperienceTaskText.Derive(
            [
                User("Deploy service X to prod"),
                new ChatMessage(ChatRole.Assistant, "Deployment failed: timeout."),
                User("and retry"),
            ]));
    }

    [Fact]
    public void A_latest_message_of_64_characters_or_more_stands_alone()
    {
        var latest = new string('a', 64);

        Assert.Equal(latest, ExperienceTaskText.Derive([User("Deploy service X to prod"), User(latest)]));
        Assert.Equal(
            "Deploy service X to prod — " + latest[..63],
            ExperienceTaskText.Derive([User("Deploy service X to prod"), User(latest[..63])]));
    }

    [Fact]
    public void Only_the_new_requests_user_message_is_used_never_history_or_injected_context()
    {
        const string request = "Why is the build red on main since the dependency bump this morning?";
        var derived = ExperienceTaskText.Derive(
        [
            FromHistory(User("Rotate the signing keys for tenant 9 and publish the new JWKS document")),
            FromHistory(User("an older follow-up")),
            FromProvider(User("Injected context: a lesson about refunds that is long enough to stand alone easily.")),
            User(request),
            FromProvider(User("Another injected message after the request.")),
        ]);

        Assert.Equal(request, derived);
    }

    [Fact]
    public void The_latest_request_is_never_older_than_the_last_history_message()
    {
        Assert.Null(ExperienceTaskText.Derive(
        [
            User("Why is the build red on main since the dependency bump this morning?"),
            FromHistory(new ChatMessage(ChatRole.Assistant, "a replayed answer")),
        ]));
    }

    [Fact]
    public void An_image_only_new_input_after_history_derives_nothing()
    {
        Assert.Null(ExperienceTaskText.Derive(
        [
            FromHistory(User("Deploy service X to prod")),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
        ]));
    }

    [Fact]
    public void A_custom_source_is_never_the_latest_request_nor_the_previous_message()
    {
        var custom = new AgentRequestMessageSourceType("Contoso.Memory");
        var fromCustom = User("Rotate the signing keys for tenant 9").WithAgentRequestMessageSource(custom, "contoso");

        Assert.Null(ExperienceTaskText.Derive([fromCustom]));
        Assert.Equal("and retry", ExperienceTaskText.Derive([fromCustom, User("and retry")]));
        Assert.Equal(
            "Deploy service X to prod \u2014 and retry",
            ExperienceTaskText.Derive([FromHistory(User("Deploy service X to prod")), fromCustom, User("and retry")]));
    }

    [Fact]
    public void The_follow_up_threshold_can_be_tuned_or_turned_off()
    {
        Assert.Equal(64, ExperienceTaskText.FollowUpThreshold);
        Assert.Equal(" \u2014 ", ExperienceTaskText.FollowUpSeparator);
        ChatMessage[] messages = [User("Deploy service X to prod"), User("and retry")];

        Assert.Equal("and retry", ExperienceTaskText.Derive(messages, 512, followUpThreshold: 0));
        Assert.Equal("and retry", ExperienceTaskText.Derive(messages, 512, followUpThreshold: 9));
        Assert.Equal("Deploy service X to prod \u2014 and retry", ExperienceTaskText.Derive(messages, 512, followUpThreshold: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceTaskText.Derive(messages, 512, followUpThreshold: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceTaskText.Derive(messages, 0, followUpThreshold: 64));
    }

    [Fact]
    public void A_history_message_alone_is_never_the_latest_request()
    {
        Assert.Null(ExperienceTaskText.Derive([FromHistory(User("Deploy service X to prod"))]));
    }

    [Fact]
    public void A_follow_up_joins_the_previous_turns_request_from_chat_history()
    {
        var derived = ExperienceTaskText.Derive(
        [
            FromHistory(User("Deploy service X to prod")),
            FromHistory(new ChatMessage(ChatRole.Assistant, "The deploy timed out.")),
            User("and retry"),
        ]);

        Assert.Equal("Deploy service X to prod \u2014 and retry", derived);
    }

    [Fact]
    public void The_previous_message_is_never_injected_context_nor_a_block()
    {
        var stamped = FromHistory(User("a block replayed from history without its marker text"));
        stamped.AdditionalProperties = new AdditionalPropertiesDictionary { [ExperienceContextProvider.HistoricalReferenceKey] = true };

        var derived = ExperienceTaskText.Derive(
        [
            FromHistory(User("Deploy service X to prod")),
            FromHistory(User(HistoricalReferenceWriter.BlockBegin + "\nold block\n" + HistoricalReferenceWriter.BlockEnd)),
            stamped,
            FromProvider(User("Injected: check the lock table")),
            User("and retry"),
        ]);

        Assert.Equal("Deploy service X to prod \u2014 and retry", derived);
    }

    [Fact]
    public void A_short_request_that_stands_alone_is_joined_too_the_rule_is_length_only()
    {
        Assert.Equal(
            "Deploy service X to prod \u2014 Why is the build red?",
            ExperienceTaskText.Derive([FromHistory(User("Deploy service X to prod")), User("Why is the build red?")]));
    }

    [Fact]
    public void A_long_previous_message_is_cut_so_the_whole_follow_up_fits()
    {
        var derived = ExperienceTaskText.Derive([User(new string('p', 600)), User("and retry")]);

        Assert.NotNull(derived);
        Assert.EndsWith(" \u2014 and retry", derived, StringComparison.Ordinal);
        Assert.Equal(512, derived!.Length);
        Assert.Equal(new string('p', 512 - " \u2014 and retry".Length), derived[..^" \u2014 and retry".Length]);
    }

    [Fact]
    public void A_previous_message_cut_at_a_space_leaves_no_double_space_and_no_dangling_separator()
    {
        // Room for "abc " -> trimmed to "abc".
        Assert.Equal("abc \u2014 and retry", ExperienceTaskText.Derive([User("abc def ghi"), User("and retry")], 16));

        // No room for the previous message at all: the latest alone, never " — and retry" or "x —".
        Assert.Equal("and retry", ExperienceTaskText.Derive([User("Deploy service X to prod"), User("and retry")], 12));
        Assert.Equal("D \u2014 and retry", ExperienceTaskText.Derive([User("Deploy service X to prod"), User("and retry")], 13));
        Assert.Equal("De \u2014 and retry", ExperienceTaskText.Derive([User("Deploy service X to prod"), User("and retry")], 14));
    }

    [Fact]
    public void A_latest_request_longer_than_the_maximum_is_cut_and_the_previous_dropped()
    {
        Assert.Equal("and r", ExperienceTaskText.Derive([User("Deploy service X to prod"), User("and retry")], 5));
    }

    [Fact]
    public void A_maximum_too_small_for_the_first_character_derives_nothing()
    {
        Assert.Null(ExperienceTaskText.Derive([User("\U0001F600 deploy")], 1));
        Assert.Equal("\U0001F600", ExperienceTaskText.Derive([User("\U0001F600 deploy")], 2));
    }

    [Fact]
    public void A_message_carrying_the_block_begin_marker_is_skipped()
    {
        var block = HistoricalReferenceWriter.BlockBegin + "\nThese records summarize earlier runs.\n" + HistoricalReferenceWriter.BlockEnd;

        Assert.Equal("Triage the stuck refund", ExperienceTaskText.Derive([User("Triage the stuck refund"), User(block)]));
        Assert.Null(ExperienceTaskText.Derive([User(block)]));
    }

    [Fact]
    public void A_user_who_pastes_an_earlier_block_into_a_prompt_gets_no_derived_text_from_that_message()
    {
        var pasted = "What does this mean for my refund?\n" + HistoricalReferenceWriter.BlockBegin + "\n--- RECORD 1 ---\n"
            + HistoricalReferenceWriter.BlockEnd;

        // The whole message is skipped, the user's own question included.
        Assert.Null(ExperienceTaskText.Derive([User(pasted)]));
        Assert.Equal(
            "Refund ticket 42 is stuck in pending and the customer is asking again today",
            ExperienceTaskText.Derive([User("Refund ticket 42 is stuck in pending and the customer is asking again today"), User(pasted)]));
    }

    [Fact]
    public void A_block_marker_split_by_an_invisible_character_is_still_recognised()
    {
        var split = HistoricalReferenceWriter.BlockBegin.Insert(10, "​") + " records follow";

        Assert.Null(ExperienceTaskText.Derive([User(split)]));
    }

    [Fact]
    public void A_message_stamped_as_a_historical_reference_is_skipped()
    {
        var stamped = User("a stamped block whose text carries no marker at all, long enough to stand alone");
        stamped.AdditionalProperties = new AdditionalPropertiesDictionary { [ExperienceContextProvider.HistoricalReferenceKey] = true };

        Assert.Equal("Fix the flaky test", ExperienceTaskText.Derive([User("Fix the flaky test"), stamped]));
    }

    [Fact]
    public void A_long_text_is_clipped_to_512_characters()
    {
        var derived = ExperienceTaskText.Derive([User(new string('x', 10_000))]);

        Assert.Equal(ExperienceTaskText.DefaultMaxLength, derived!.Length);
        Assert.Equal(512, ExperienceTaskText.DefaultMaxLength);
    }

    [Fact]
    public void A_clip_never_splits_a_surrogate_pair()
    {
        // 511 letters, then an emoji whose high surrogate would be character 512.
        var text = new string('x', 511) + "\U0001F600" + new string('y', 100);

        var derived = ExperienceTaskText.Derive([User(text)]);

        Assert.Equal(new string('x', 511), derived);
        Assert.False(char.IsHighSurrogate(derived![^1]));

        // A pair that fits whole is kept whole.
        var fits = new string('x', 510) + "\U0001F600" + new string('y', 100);
        Assert.Equal(new string('x', 510) + "\U0001F600", ExperienceTaskText.Derive([User(fits)]));
    }

    [Fact]
    public void Whitespace_is_collapsed_and_control_and_format_characters_are_removed()
    {
        var derived = ExperienceTaskText.Derive(
            [User("  Refund\tticket\r\n\n42 ​is\u0007 stu​ck ‮in⁦ pending\U000E0041  ")]);

        Assert.Equal("Refund ticket 42 is stuck in pending", derived);
    }

    [Fact]
    public void A_joiner_between_letters_or_symbols_is_kept_and_elsewhere_removed()
    {
        // Persian: ZWNJ between letters; Devanagari: ZWJ after a virama (a mark); an emoji ZWJ sequence.
        Assert.Equal("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645", ExperienceTaskText.Derive([User("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")]));
        Assert.Equal("\u0915\u094D\u200D\u0937", ExperienceTaskText.Derive([User("\u0915\u094D\u200D\u0937")]));
        Assert.Equal("\U0001F469\u200D\U0001F4BB fix it", ExperienceTaskText.Derive([User("\U0001F469\u200D\U0001F4BB fix it")]));

        // At an edge, beside a space or a digit: removed. A doubled joiner keeps one.
        Assert.Equal("a b", ExperienceTaskText.Derive([User("\u200Da \u200Db\u200D")]));
        Assert.Equal("12", ExperienceTaskText.Derive([User("1\u200D2")]));
        Assert.Equal("a\u200Db", ExperienceTaskText.Derive([User("a\u200D\u200Db")]));
    }

    [Fact]
    public void A_joiner_is_decided_on_the_next_kept_character_not_the_next_raw_one()
    {
        // A removed format character between the joiner and the next letter does not cost the joiner.
        Assert.Equal("\u0645\u06CC\u200C\u062E", ExperienceTaskText.Derive([User("\u0645\u06CC\u200C\u200B\u062E")]));

        // A removed character, then a space or a digit: the joiner goes.
        Assert.Equal("\u0645 \u062E", ExperienceTaskText.Derive([User("\u0645\u200C\u200B \u062E")]));
        Assert.Equal("a1", ExperienceTaskText.Derive([User("a\u200D\u2060" + "1")]));
    }

    [Fact]
    public void A_cut_never_ends_with_a_joiner_or_a_partial_flag()
    {
        // The cut lands right after a kept joiner: it is trimmed, both alone and before the separator.
        var joined = "\u0645\u06CC\u200C\u062E\u0648";
        Assert.Equal("\u0645\u06CC", ExperienceTaskText.Derive([User(joined)], 3));
        Assert.Equal("\u0645\u06CC \u2014 and retry", ExperienceTaskText.Derive([User(joined), User("and retry")], 3 + 12));

        // The cut lands inside a subdivision flag's tags: the unfinished tags go, the flag base stays.
        var scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";
        Assert.Equal("\U0001F3F4", ExperienceTaskText.Derive([User(scotland + " rugby")], 6));
    }

    [Fact]
    public void A_subdivision_flags_tags_are_kept_and_tags_anywhere_else_removed()
    {
        var scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";
        Assert.Equal(scotland + " rugby fixtures", ExperienceTaskText.Derive([User(scotland + " rugby fixtures")]));

        // Tags after another character, or a flag run with no cancel tag, or one too long: removed.
        Assert.Equal("ahi", ExperienceTaskText.Derive([User("a\U000E0068\U000E0069\U000E007F" + "hi")]));
        Assert.Equal("\U0001F3F4 x", ExperienceTaskText.Derive([User("\U0001F3F4\U000E0067\U000E0062 x")]));
        var smuggled = "\U0001F3F4" + string.Concat(Enumerable.Repeat("\U000E0041", 9)) + "\U000E007F";
        Assert.Equal("\U0001F3F4 x", ExperienceTaskText.Derive([User(smuggled + " x")]));
    }

    [Fact]
    public void A_block_marker_split_by_a_kept_joiner_is_still_recognised()
    {
        var split = HistoricalReferenceWriter.BlockBegin.Replace("BEGIN", "BE\u200DGIN", StringComparison.Ordinal);

        Assert.Null(ExperienceTaskText.Derive([User(split + " records follow")]));
    }

    [Fact]
    public void No_unicode_normalization_is_applied()
    {
        // A decomposed e + combining acute stays decomposed.
        Assert.Equal("cafe\u0301 order", ExperienceTaskText.Derive([User("cafe\u0301 order")]));
    }

    [Fact]
    public void An_unpaired_surrogate_is_removed()
    {
        Assert.Equal("ab", ExperienceTaskText.Derive([User("a\uD800b")]));
    }

    [Fact]
    public void Only_text_parts_count_joined_with_a_space()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [
                new TextContent("Look at"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
                new TextContent("this screenshot of the failing deploy"),
            ]);

        Assert.Equal("Look at this screenshot of the failing deploy", ExperienceTaskText.Derive([message]));
    }

    [Fact]
    public void No_user_text_derives_nothing()
    {
        Assert.Null(ExperienceTaskText.Derive([]));
        Assert.Null(ExperienceTaskText.Derive(
        [
            new ChatMessage(ChatRole.System, "You are a support agent."),
            new ChatMessage(ChatRole.Assistant, "Calling the ledger."),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "ledger row 42")]),
        ]));
        Assert.Null(ExperienceTaskText.Derive(
            [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")])]));
        Assert.Null(ExperienceTaskText.Derive([User(" \t​\n ")]));
    }

    [Fact]
    public void A_user_message_with_no_text_does_not_hide_the_one_before_it()
    {
        Assert.Equal(
            "Summarize the incident report",
            ExperienceTaskText.Derive(
            [
                User("Summarize the incident report"),
                new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
            ]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void An_invalid_max_length_is_refused(int maxLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceTaskText.Derive([User("task")], maxLength));
    }

    [Fact]
    public void The_max_length_bounds_are_inclusive()
    {
        Assert.Equal("t", ExperienceTaskText.Derive([User("task")], 1));
        Assert.Equal(4096, ExperienceTaskText.Derive([User(new string('x', 5000))], ExperienceCandidateQuery.MaxTaskTextLength)!.Length);
    }

    [Fact]
    public void A_clip_ending_on_a_space_is_trimmed()
    {
        Assert.Equal("abc", ExperienceTaskText.Derive([User("abc def")], 4));
    }

    [Fact]
    public void Null_messages_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ExperienceTaskText.Derive(null!));
    }

    // ---- The context properties ------------------------------------------------------------------

    [Fact]
    public void Both_contexts_expose_the_derived_text_and_compute_it_once()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var counting = new CountingMessages([User("Deploy service X to prod"), User("and retry")]);

        var run = new ExperienceRunContext(counting, null, agent);
        Assert.Equal("Deploy service X to prod — and retry", run.DerivedTaskText);
        Assert.Equal("Deploy service X to prod — and retry", run.DerivedTaskText);
        Assert.Equal(1, counting.Enumerations);

        var injection = new ExperienceInjectionContext([User("Refund ticket 42 is stuck in pending")], null, agent);
        Assert.Equal("Refund ticket 42 is stuck in pending", injection.DerivedTaskText);
        Assert.Null(new ExperienceInjectionContext([], null, agent).DerivedTaskText);
    }

    [Fact]
    public void A_with_copy_derives_on_its_own_and_never_makes_the_original_derive_again()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var counting = new CountingMessages([User("Deploy service X to prod")]);
        var original = new ExperienceRunContext(counting, null, agent);

        Assert.Equal("Deploy service X to prod", original.DerivedTaskText);
        Assert.Equal(1, counting.Enumerations);

        var copy = original with { };
        Assert.Equal("Deploy service X to prod", copy.DerivedTaskText);
        Assert.Equal(2, counting.Enumerations);

        Assert.Equal("Deploy service X to prod", original.DerivedTaskText);
        Assert.Equal("Deploy service X to prod", copy.DerivedTaskText);
        Assert.Equal(2, counting.Enumerations);
    }

    [Fact]
    public void Concurrent_first_reads_derive_once()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var counting = new CountingMessages([User("Deploy service X to prod")]);
        var context = new ExperienceRunContext(counting, null, agent);

        Parallel.For(0, 32, _ => Assert.Equal("Deploy service X to prod", context.DerivedTaskText));

        Assert.Equal(1, counting.Enumerations);
    }

    [Fact]
    public void The_text_reflects_the_messages_as_of_the_first_read()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var messages = new List<ChatMessage> { User("first task") };
        var context = new ExperienceInjectionContext(messages, null, agent);

        Assert.Equal("first task", context.DerivedTaskText);
        messages.Add(User("a later message appended to the same list"));

        Assert.Equal("first task", context.DerivedTaskText);
    }

    [Fact]
    public void A_with_expression_that_replaces_the_messages_derives_again()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var original = new ExperienceInjectionContext([User("first task")], null, agent);
        Assert.Equal("first task", original.DerivedTaskText);

        var changed = original with { Messages = [User("second task")] };

        Assert.Equal("second task", changed.DerivedTaskText);
        Assert.Equal("first task", original.DerivedTaskText);
    }

    [Fact]
    public void The_property_changes_neither_equality_nor_what_ToString_shows()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        IReadOnlyList<ChatMessage> messages = [User("secret-ish prompt text")];
        var read = new ExperienceInjectionContext(messages, null, agent);
        var unread = new ExperienceInjectionContext(messages, null, agent);
        _ = read.DerivedTaskText;

        Assert.Equal(read, unread);
        Assert.Equal(read.GetHashCode(), unread.GetHashCode());
        Assert.NotEqual(read, new ExperienceInjectionContext([User("secret-ish prompt text")], null, agent));
        Assert.DoesNotContain("secret-ish", read.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("ExperienceInjectionContext { Messages = ", read.ToString(), StringComparison.Ordinal);

        var run = new ExperienceRunContext(messages, null, agent);
        _ = run.DerivedTaskText;
        Assert.Equal(run, new ExperienceRunContext(messages, null, agent));
        Assert.DoesNotContain("secret-ish", run.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_with_a_derivation_source_derives_from_it_until_Messages_is_replaced()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        ChatMessage[] request = [FromHistory(User("Deploy service X to prod")), User("and retry")];
        var original = new ExperienceInjectionContext([User("and retry")], null, agent).WithDerivationSource(request);

        Assert.Equal("Deploy service X to prod \u2014 and retry", original.DerivedTaskText);
        Assert.Equal("second task", (original with { Messages = [User("second task")] }).DerivedTaskText);
        Assert.Equal("Deploy service X to prod \u2014 and retry", (original with { Session = null }).DerivedTaskText);
    }

    [Fact]
    public void A_throwing_sequence_throws_on_the_first_read_and_is_never_read_again()
    {
        var agent = new ChatClientAgent(new RecordingChatClient());
        var throwing = new ThrowingMessages();
        var context = new ExperienceRunContext(throwing, null, agent);

        Assert.Throws<InvalidOperationException>(() => context.DerivedTaskText);
        Assert.Null(context.DerivedTaskText);
        Assert.Null(context.DerivedTaskText);
        Assert.Equal(1, throwing.Enumerations);
    }

    /// <summary>A sequence that throws when enumerated, and counts how often it was asked.</summary>
    private sealed class ThrowingMessages : IEnumerable<ChatMessage>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            Enumerations++;
            throw new InvalidOperationException("the host's sequence failed");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A sequence that counts how often it is enumerated.</summary>
    private sealed class CountingMessages(IEnumerable<ChatMessage> inner) : IEnumerable<ChatMessage>
    {
        private int _enumerations;

        public int Enumerations => Volatile.Read(ref _enumerations);

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerations);
            return inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
