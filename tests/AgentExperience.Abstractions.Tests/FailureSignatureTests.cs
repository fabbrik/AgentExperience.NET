using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Story 28.2: a failure signature is derived deterministically from a failed call's tool name and error, the same
/// failure with different IDs, paths, numbers or timestamps collides, and distinct real-world failures stay distinct.
/// </summary>
public class FailureSignatureTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Each rule, alone, in the documented order.</summary>
    [Theory]
    [InlineData("  Boom  ", "boom")]
    [InlineData("first line\nsecond line", "first line")]
    [InlineData("\r\n  first line\r\nsecond", "first line")]
    [InlineData("Record 3F2504E0-4F89-11D3-9A0C-0305E82C3301 not found", "record <guid> not found")]
    [InlineData("commit deadbeef42 missing", "commit <hex> missing")]
    [InlineData("pointer 0x7ffee1234567 invalid", "pointer <hex> invalid")]
    [InlineData("the word acceptable stays", "the word acceptable stays")]
    [InlineData("No such file: /home/runner/work/app/src/main.py", "no such file: <path>.py")]
    [InlineData(@"Could not find C:\Users\dev\repo\bin\Debug\app.dll", "could not find <path>.dll")]
    [InlineData("cannot open ./scripts/build", "cannot open <path>")]
    [InlineData("module 'requests' has no attribute \"get\"", "module <str> has no attribute <str>")]
    [InlineData("expired at 2026-10-30T12:00:01.123Z", "expired at <time>")]
    [InlineData("since 2026-10-30", "since <time>")]
    [InlineData("line 42, column 7: 3.14 is out of range", "line <n>, column <n>: <n> is out of range")]
    [InlineData("a   b \t c", "a b c")]
    [InlineData("and/or", "<path>")]
    [InlineData("a / b", "a / b")]
    [InlineData("", "")]
    public void Each_normalization_rule_does_what_it_says(string error, string expected)
    {
        Assert.Equal(expected, ExperienceFailureSignature.NormalizeExcerpt(error));
    }

    [Fact]
    public void The_excerpt_is_cut_at_500_characters_after_trimming_and_never_inside_a_surrogate_pair()
    {
        var plain = new string('x', 600);
        Assert.Equal(new string('x', 500), ExperienceFailureSignature.NormalizeExcerpt("   " + plain));

        // A surrogate pair straddling the cut is dropped whole.
        var straddling = new string('x', 499) + "\U0001F600" + "tail";
        Assert.Equal(new string('x', 499), ExperienceFailureSignature.NormalizeExcerpt(straddling));
    }

    /// <summary>The same failure, seen in two runs with different identifiers, collides.</summary>
    [Theory]
    [InlineData(
        "FileNotFoundException: Could not find file '/tmp/build-8812/out/report.json'.",
        "FileNotFoundException: Could not find file '/tmp/build-1203/out/report.json'.")]
    [InlineData(
        "  File \"/home/alice/proj/tests/test_api.py\", line 42, in test_get",
        "  File \"/home/bob/checkout/tests/test_api.py\", line 97, in test_get")]
    [InlineData(
        "ModuleNotFoundError: No module named 'requests'",
        "ModuleNotFoundError: No module named 'httpx'")]
    [InlineData(
        "HTTP 503 from https://api.example.com/v1/orders/18823 after 30.5s",
        "HTTP 503 from https://api.example.com/v1/orders/77 after 2.25s")]
    [InlineData(
        "Order 6f9619ff-8b86-d011-b42d-00c04fc964ff was locked at 2026-10-29T08:00:00Z",
        "Order 0c7f2a1e-3b4d-4e5f-8a9b-1c2d3e4f5a6b was locked at 2026-10-30T17:45:12+02:00")]
    [InlineData(
        "error: failed to push some refs to 'git@github.com:acme/app.git' (commit a1b2c3d4e5f6)",
        "error: failed to push some refs to 'git@github.com:acme/other.git' (commit 9f8e7d6c5b4a)")]
    [InlineData(
        "Process exited with code 2\n  at stack frame 1\n  at stack frame 2",
        "Process exited with code 2\n  at a different stack")]
    [InlineData(
        "npm ERR! code E404 404 Not Found - GET https://registry.npmjs.org/left-pad-7",
        "npm ERR! code E404 404 Not Found - GET https://registry.npmjs.org/left-pad-12")]
    public void The_same_error_with_different_ids_paths_and_numbers_collides(string first, string second)
    {
        Assert.Equal(ExperienceFailureSignature.NormalizeExcerpt(first), ExperienceFailureSignature.NormalizeExcerpt(second));
        Assert.Equal(ExperienceFailureSignature.From("run", first), ExperienceFailureSignature.From("run", second));
    }

    /// <summary>Different real-world failures keep different fingerprints.</summary>
    [Fact]
    public void Distinct_real_world_errors_stay_distinct()
    {
        string[] errors =
        [
            "FileNotFoundException: Could not find file '/tmp/out/report.json'.",
            "UnauthorizedAccessException: Access to the path '/tmp/out/report.json' is denied.",
            "ModuleNotFoundError: No module named 'requests'",
            "ImportError: cannot import name 'get' from 'requests'",
            "error CS0246: The type or namespace name 'Foo' could not be found",
            "error CS0103: The name 'Foo' does not exist in the current context",
            "npm ERR! code E404 404 Not Found",
            "npm ERR! code EACCES permission denied",
            "fatal: not a git repository (or any of the parent directories): .git",
            "fatal: refusing to merge unrelated histories",
            "connect ECONNREFUSED 127.0.0.1:5432",
            "getaddrinfo ENOTFOUND db.internal",
            "Process exited with code 2",
            "Process exited with code 1",
            "TimeoutException: The operation has timed out.",
            "TaskCanceledException: A task was canceled.",
            "AssertionError: expected 200 but got 404",
            "AssertionError: expected list to be empty",
            "Permission denied (publickey).",
            "No space left on device",
        ];

        var keys = errors.Select(error => ExperienceFailureSignature.From("tool", error)!.Key).ToList();
        Assert.Equal(errors.Length, keys.Distinct(StringComparer.Ordinal).Count());

        // The two exit codes normalize to one excerpt; the error class is what keeps them apart.
        Assert.Equal(
            ExperienceFailureSignature.FingerprintOf("Process exited with code 2"),
            ExperienceFailureSignature.FingerprintOf("Process exited with code 1"));
    }

    [Fact]
    public void The_fingerprint_is_the_first_16_bytes_of_the_sha256_of_the_normalized_excerpt_in_lowercase_hex()
    {
        const string Error = "No such file: /home/runner/work/app/src/main.py";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("no such file: <path>.py")).AsSpan(0, 16));

        Assert.Equal(expected, ExperienceFailureSignature.FingerprintOf(Error));
        Assert.Matches("^[0-9a-f]{32}$", ExperienceFailureSignature.FingerprintOf(Error));
    }

    [Theory]
    [InlineData("System.TimeoutException: The operation has timed out.", "TimeoutException")]
    [InlineData("Process exited with code 2", "exit 2")]
    [InlineData("Response status code does not indicate success: 503 (Service Unavailable).", "HTTP 503")]
    [InlineData("ENOENT: no such file or directory, IOException later", "ENOENT")]
    [InlineData("the call timed out", "Timeout")]
    [InlineData("something odd happened", "unclassified error")]
    [InlineData("", "unclassified error")]
    public void The_error_class_is_the_first_token_the_failed_marker_shows(string error, string errorClass)
    {
        var signature = ExperienceFailureSignature.From("run_tests", error)!;

        Assert.Equal("run_tests", signature.ToolName);
        Assert.Equal(errorClass, signature.ErrorClass);
        Assert.Equal($"run_tests\u001F{errorClass}\u001F{signature.Fingerprint}", signature.Key);
    }

    [Fact]
    public void A_call_without_an_error_or_with_an_unkeyable_tool_name_has_no_signature()
    {
        Assert.Null(ExperienceFailureSignature.From(Call("run_tests", error: null)));
        Assert.NotNull(ExperienceFailureSignature.From(Call("run_tests", error: "  ")));

        Assert.Null(ExperienceFailureSignature.From(null, "boom"));
        Assert.Null(ExperienceFailureSignature.From("  ", "boom"));
        Assert.Null(ExperienceFailureSignature.From(new string('t', ExperienceFailureSignature.MaxToolNameLength + 1), "boom"));
        Assert.NotNull(ExperienceFailureSignature.From(new string('t', ExperienceFailureSignature.MaxToolNameLength), "boom"));
        Assert.Null(ExperienceFailureSignature.From("run\u001Ftests", "boom"));
        Assert.Null(ExperienceFailureSignature.From("run\ntests", "boom"));
        Assert.Null(ExperienceFailureSignature.From("run\uD800tests", "boom"));
        Assert.NotNull(ExperienceFailureSignature.From("run_\U0001F600", "boom"));
    }

    [Fact]
    public void The_derivation_does_not_depend_on_the_current_culture()
    {
        const string Error = "INVALID İNPUT at LINE 4: 'TITLE'";
        var invariant = ExperienceFailureSignature.From("tool", Error);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(invariant, ExperienceFailureSignature.From("tool", Error));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_records_signatures_are_in_call_order_deduplicated_and_resolved_only_after_a_later_success_in_a_verified_run()
    {
        var record = Record(
            TaskVerificationStatus.Verified,
            Attempt(1,
                Call("build", "error CS0246: The type 'Foo' could not be found", sequence: 0),
                Call("run_tests", "Process exited with code 2 after 41 tests", sequence: 1)),
            Attempt(0,
                Call("restore", "NU1101: Unable to find package Foo.Bar 1.2.3", sequence: 1),
                Call("restore", error: null, sequence: 2),
                Call("build", "error CS0246: The type 'Foo' could not be found", sequence: 0)),
            Attempt(2,
                Call("run_tests", "Process exited with code 2 after 12 tests", sequence: 0),
                Call("run_tests", error: null, sequence: 1),
                Call("lint", "exit 1", sequence: 2)));

        var signatures = ExperienceFailureSignatures.Of(record);

        // Attempt 0 first (by sequence number), then calls by sequence number; the repeated build and run_tests failures once.
        Assert.Equal(["build", "restore", "run_tests", "lint"], signatures.Select(s => s.Signature.ToolName));
        Assert.Equal(
            [
                ExperienceSignatureResolution.Resolved,   // build: a later restore and run_tests succeeded
                ExperienceSignatureResolution.Resolved,   // restore: its own retry succeeded
                ExperienceSignatureResolution.Resolved,   // run_tests: last occurrence, then a success
                ExperienceSignatureResolution.Unresolved, // lint: nothing after it
            ],
            signatures.Select(s => s.Resolution));
        Assert.Equal(signatures.Count, signatures.Select(s => s.Signature.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_failure_that_comes_back_after_a_success_is_not_resolved_by_it()
    {
        var record = Record(
            TaskVerificationStatus.Verified,
            Attempt(0,
                Call("deploy", "HTTP 503", sequence: 0),
                Call("status", error: null, sequence: 1),
                Call("deploy", "HTTP 503", sequence: 2)));

        var signature = Assert.Single(ExperienceFailureSignatures.Of(record));
        Assert.Equal(ExperienceSignatureResolution.Unresolved, signature.Resolution);
    }

    [Theory]
    [InlineData(TaskVerificationStatus.Failed)]
    [InlineData(TaskVerificationStatus.Unknown)]
    public void Nothing_is_resolved_in_a_run_that_was_not_verified(TaskVerificationStatus status)
    {
        var record = Record(status, Attempt(0, Call("build", "exit 1", sequence: 0), Call("build", error: null, sequence: 1)));

        Assert.Equal(ExperienceSignatureResolution.Unresolved, Assert.Single(ExperienceFailureSignatures.Of(record)).Resolution);
    }

    [Fact]
    public void A_record_without_failed_calls_has_no_signatures()
    {
        Assert.Empty(ExperienceFailureSignatures.Of(Record(TaskVerificationStatus.Verified)));
        Assert.Empty(ExperienceFailureSignatures.Of(Record(TaskVerificationStatus.Verified, Attempt(0, Call("build", error: null, sequence: 0)))));
    }

    private static ToolCallRecord Call(string tool, string? error, int sequence = 0) =>
        new(Guid.NewGuid(), sequence, tool, new Dictionary<string, object?>(), Time, TimeSpan.FromSeconds(1), error is null ? "ok" : null, error);

    private static Attempt Attempt(int sequence, params ToolCallRecord[] calls) =>
        new(Guid.NewGuid(), sequence, Time, TimeSpan.FromSeconds(1), calls, null, null);

    private static ExperienceRecord Record(TaskVerificationStatus status, params Attempt[] attempts) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new Scope("tenant", "app", "project"),
        "task",
        null,
        attempts,
        new Outcome(status, [], null, Time),
        1,
        null,
        new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        new Provenance("test", null, Time, null),
        ExperienceStatus.Validated,
        0.8,
        0,
        0,
        0,
        Time,
        Time);
}
