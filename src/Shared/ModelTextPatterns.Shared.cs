using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Shared source (story 14.3): the fixed shapes the content guard looks for in model-authored text. Core's
// screening (ModelAuthoredContentGuard) and the MAF model reflector (its Producer check) both link this file, so
// the two agree on what a URL, a hostname or an IP address is. It works on text the caller has already folded
// (invisible characters removed, NFKC) and depends on nothing but the BCL. Each project defines exactly one of the
// constants below, which puts the type in that project's own namespace.
#if AGENTEXPERIENCE_CORE && AGENTEXPERIENCE_MAF
#error Define exactly one of AGENTEXPERIENCE_CORE and AGENTEXPERIENCE_MAF.
#elif AGENTEXPERIENCE_CORE
namespace AgentExperience.Core.Reflections;
#elif AGENTEXPERIENCE_MAF
namespace AgentExperience.MicrosoftAgentFramework.Reflections;
#else
#error Define AGENTEXPERIENCE_CORE or AGENTEXPERIENCE_MAF to select the namespace of the shared patterns.
#endif

/// <summary>
/// The fixed, deterministic shapes the model-authored content guard refuses or compares: links (URLs, hostnames,
/// IP addresses, colon-only schemes, UNC paths), instruction-override phrasing, credential shapes, and words mixing
/// Latin with Cyrillic or Greek letters. A heuristic: see the finalization guide's "Limits of model-authored lessons".
/// </summary>
internal static class ModelTextPatterns
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// Generic top-level domains a hostname may end in: a fixed, deliberately small set. Left out because they
    /// collide with .NET member names or file extensions: <c>name</c>, <c>services</c>, <c>run</c>, <c>build</c>,
    /// <c>store</c>, <c>shop</c>, <c>page</c>, <c>link</c>, <c>live</c>, <c>int</c> (<c>SqlDbType.Int</c>),
    /// <c>test</c>, <c>invalid</c>, <c>zip</c> and <c>mov</c> (file extensions).
    /// </summary>
    private static readonly string[] GenericTlds =
    [
        "com", "net", "org", "edu", "gov", "mil", "arpa", "info", "biz", "pro", "mobi", "aero", "asia", "coop",
        "jobs", "museum", "tel", "travel", "xxx", "xyz", "online", "site", "website", "top", "club", "tech", "cloud",
        "app", "dev", "icu", "vip", "win", "bid", "loan", "example", "localhost", "onion",
    ];

    /// <summary>
    /// The assigned ISO 3166-1 country codes, plus <c>uk</c>, <c>eu</c>, <c>su</c> and <c>ac</c>, less those that
    /// collide with common file extensions or .NET member names and would refuse ordinary lessons: <c>md</c>
    /// (Markdown), <c>py</c> (Python), <c>rs</c> (Rust), <c>sh</c> (shell), <c>pl</c> and <c>pm</c> (Perl),
    /// <c>ps</c> (PostScript), <c>so</c> (shared objects), <c>cc</c> and <c>mm</c> (C++ and Objective-C++),
    /// <c>mk</c> (makefiles), <c>am</c> (automake), <c>ml</c> (OCaml), <c>tf</c> (Terraform), <c>mo</c> (gettext),
    /// <c>gd</c> (GDScript), <c>id</c> (<c>record.Id</c>), <c>in</c> (<c>Console.In</c>), <c>is</c>
    /// (<c>It.Is</c>) and <c>as</c> (<c>x.As&lt;T&gt;()</c>). (<c>cs</c> and <c>ts</c> are not assigned.)
    /// </summary>
    private static readonly string[] CountryTlds =
    [
        "ad", "ae", "af", "ag", "ai", "al", "ao", "aq", "ar", "at", "au", "aw", "ax", "az", "ba", "bb", "bd", "be",
        "bf", "bg", "bh", "bi", "bj", "bl", "bm", "bn", "bo", "bq", "br", "bs", "bt", "bv", "bw", "by", "bz", "ca",
        "cd", "cf", "cg", "ch", "ci", "ck", "cl", "cm", "cn", "co", "cr", "cu", "cv", "cw", "cx", "cy", "cz", "de",
        "dj", "dk", "dm", "do", "dz", "ec", "ee", "eg", "eh", "er", "es", "et", "fi", "fj", "fk", "fm", "fo", "fr",
        "ga", "gb", "ge", "gf", "gg", "gh", "gi", "gl", "gm", "gn", "gp", "gq", "gr", "gs", "gt", "gu", "gw", "gy",
        "hk", "hm", "hn", "hr", "ht", "hu", "ie", "il", "im", "io", "iq", "ir", "it", "je", "jm", "jo", "jp", "ke",
        "kg", "kh", "ki", "km", "kn", "kp", "kr", "kw", "ky", "kz", "la", "lb", "lc", "li", "lk", "lr", "ls", "lt",
        "lu", "lv", "ly", "ma", "mc", "me", "mf", "mg", "mh", "mm", "mn", "mp", "mq", "mr", "ms", "mt", "mu", "mv",
        "mw", "mx", "my", "mz", "na", "nc", "ne", "nf", "ng", "ni", "nl", "no", "np", "nr", "nu", "nz", "om", "pa",
        "pe", "pf", "pg", "ph", "pk", "pn", "pr", "pt", "pw", "qa", "re", "ro", "ru", "rw", "sa", "sb", "sc", "sd",
        "se", "sg", "si", "sj", "sk", "sl", "sm", "sn", "sr", "ss", "st", "sv", "sx", "sy", "sz", "tc", "td", "tg",
        "th", "tj", "tk", "tl", "tm", "tn", "to", "tr", "tt", "tv", "tw", "tz", "ua", "ug", "um", "us", "uy", "uz",
        "va", "vc", "ve", "vg", "vi", "vn", "vu", "wf", "ws", "ye", "yt", "za", "zm", "zw", "uk", "eu", "su", "ac",
    ];

    private static readonly FrozenSet<string> Tlds = GenericTlds.Concat(CountryTlds.Where(tld => tld != "mm"))
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>A scheme and <c>://</c>, or <c>www.</c>, up to the next whitespace or quote. Run on lower-cased, deobfuscated text.</summary>
    private static readonly Regex UrlPattern = new(
        @"(?<![\p{L}\p{N}])[a-z][a-z0-9+.\-]*://[^\s<>""'`]+|(?<![\p{L}\p{N}\p{M}_\-.])www\.[^\s<>""'`]+",
        Options,
        MatchTimeout);

    /// <summary>A dotted token of Unicode letters, digits, marks and hyphens. Whether it is a hostname is decided by its last label.</summary>
    private static readonly Regex DottedTokenPattern = new(
        @"(?<![\p{L}\p{N}\p{M}\-])(?>[\p{L}\p{N}\p{M}\-]+)(?:\.(?>[\p{L}\p{N}\p{M}\-]+))+(?![\p{L}\p{N}\p{M}])",
        Options,
        MatchTimeout);

    /// <summary>A dotted-quad IPv4 address, or a 32-bit or dotted <c>0x</c> hex one.</summary>
    private static readonly Regex IpPattern = new(
        @"(?<![\p{L}\p{N}.])(?:\d{1,3}\.){3}\d{1,3}(?![\p{L}\p{N}]|\.\d)"
        + @"|(?<![\p{L}\p{N}])0x[0-9a-f]{8}(?![\p{L}\p{N}])"
        + @"|(?<![\p{L}\p{N}])0x[0-9a-f]{1,2}(?:\.0x[0-9a-f]{1,2}){3}(?![\p{L}\p{N}])",
        Options,
        MatchTimeout);

    /// <summary>Links refused wherever they come from: <c>data:</c>, <c>javascript:</c>, <c>vbscript:</c> and <c>file:</c> schemes, and UNC paths.</summary>
    private static readonly Regex AlwaysRefusedLinkPattern = new(
        @"(?<![\p{L}\p{N}])(?:javascript|vbscript):\S|(?<![\p{L}\p{N}])data:(?:,|[a-z]+/)|(?<![\p{L}\p{N}])file:/"
        + @"|\\\\[\p{L}\p{N}][\p{L}\p{N}.\-]*\\[\p{L}\p{N}$]",
        Options,
        MatchTimeout);

    /// <summary><c>[.]</c>, <c>(.)</c>, <c>{.}</c>, <c>[dot]</c>, <c>(dot)</c> and <c>{dot}</c>, with any spaces inside or around them.</summary>
    private static readonly Regex BracketedDotPattern = new(@"\s*[\[\(\{]\s*(?:\.|dot)\s*[\]\)\}]\s*", Options, MatchTimeout);

    /// <summary>The word <c>dot</c> between two words.</summary>
    private static readonly Regex SpelledDotPattern = new(@"(?<=[\p{L}\p{N}])\s+dot\s+(?=[\p{L}\p{N}])", Options, MatchTimeout);

    /// <summary>
    /// The credential shapes refused in model-authored text, matched on the folded text as written: private-key
    /// headers, AWS, OpenAI and Anthropic, GitHub, GitLab, Google API, Slack and JWT tokens, Azure storage
    /// connection-string keys, and a non-empty <c>password=</c> or <c>pwd=</c>.
    /// </summary>
    private static readonly Regex CredentialPattern = new(
        @"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----"
        + @"|(?:AKIA|ASIA)[A-Z0-9]{16}"
        + @"|sk-[A-Za-z0-9_\-]{20,}"
        + @"|gh[opusr]_[A-Za-z0-9]{36}"
        + @"|github_pat_[A-Za-z0-9_]{22,}"
        + @"|glpat-[A-Za-z0-9_\-]{20,}"
        + @"|AIza[0-9A-Za-z_\-]{35}"
        + @"|xox[abposr]-"
        + @"|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+"
        + @"|(?i:(?<![a-z])(?:AccountKey|SharedAccessSignature|password|pwd)\s*=[^\s;""',])",
        Options,
        MatchTimeout);

    private const string Roles =
        "assistant|admin|administrator|operator|root|superuser|system|developer|agent|ai|bot|model|hacker|jailbroken|unrestricted|unfiltered|dan";

    /// <summary>
    /// The instruction-override phrasing refused, matched on the phrase form (see <see cref="PhraseForm"/>): lower case,
    /// confusables folded, every run of non-letters one space.
    /// </summary>
    private static readonly Regex PhrasePattern = new(
        @"(?<![a-z])(?:"
        + @"(?:ignore|disregard|forget|override|bypass) (?:(?:all|any|the|your|my|these|those|every) )?(?:(?:the )?(?:previous|prior|earlier|above|preceding|former|original|system|existing) )?(?:instructions?|directions|directives|prompts?)"
        + @"|(?:ignore|disregard|forget) (?:all (?:the )?(?:previous|prior|earlier|above|preceding)|(?:the|everything) above|everything (?:before|previous|prior))"
        + @"|you are now (?:a|an|the) (?:[a-z]+ )?(?:" + Roles + @")"
        + @"|you are now in (?:[a-z]+ ){0,2}mode"
        + @"|you are now (?:unrestricted|jailbroken|unfiltered|root|admin)"
        + @"|(?:reveal|print|ignore|output|show|repeat|leak|display|dump|disclose) (?:the|your|my) (?:(?:full|entire|original|hidden|initial|secret) )?system prompts?"
        + @"|(?:do not|don t|dont|never) (?:tell|inform|notify|alert) the user"
        + @")(?![a-z])",
        Options,
        MatchTimeout);

    /// <summary><c>new instructions:</c>, matched on the lower-cased text with whitespace collapsed.</summary>
    private static readonly Regex NewInstructionsPattern = new(@"(?<![\p{L}\p{N}])new instructions\s*:", Options, MatchTimeout);

    /// <summary>The core phrases again, with every non-letter removed, so a phrase spelled with spaces or punctuation between its letters is caught.</summary>
    private static readonly string[] CollapsedPhrases =
    [
        "ignorepreviousinstructions", "ignoreallpreviousinstructions", "ignorepriorinstructions", "ignoreallpriorinstructions",
        "ignoreyourinstructions", "ignoretheaboveinstructions", "disregardpreviousinstructions",
        "disregardallpreviousinstructions", "disregardpriorinstructions", "disregardyourinstructions",
        "disregardallinstructions", "forgetpreviousinstructions", "forgetallpreviousinstructions",
        "overrideyourinstructions", "overridetheinstructions", "donottelltheuser", "donttelltheuser",
        "revealthesystemprompt", "revealyoursystemprompt", "printthesystemprompt", "printyoursystemprompt",
    ];

    /// <summary>Latin, Cyrillic and Greek letters that look alike, folded to the Latin one for phrase matching.</summary>
    private static readonly FrozenDictionary<char, char> Confusables = new Dictionary<char, char>
    {
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u0451'] = 'e', ['\u0456'] = 'i', ['\u0457'] = 'i', ['\u0458'] = 'j',
        ['\u043A'] = 'k', ['\u043E'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c', ['\u0455'] = 's', ['\u0443'] = 'y',
        ['\u0445'] = 'x', ['\u04BB'] = 'h', ['\u0501'] = 'd', ['\u051B'] = 'q', ['\u051D'] = 'w', ['\u0131'] = 'i',
        ['\u0261'] = 'g', ['\u03B1'] = 'a', ['\u03B5'] = 'e', ['\u03B9'] = 'i', ['\u03BA'] = 'k', ['\u03BD'] = 'v',
        ['\u03BF'] = 'o', ['\u03C1'] = 'p', ['\u03C4'] = 't', ['\u03C5'] = 'u', ['\u03C7'] = 'x',
    }.ToFrozenDictionary();

    /// <summary>
    /// <paramref name="lower"/> with the common ways of hiding a dot undone: the ideographic and full-width full
    /// stops, <c>[.]</c>, <c>(.)</c>, <c>{.}</c>, <c>[dot]</c> and a spelled-out <c>dot</c> between two words.
    /// Whitespace around a plain dot is not collapsed (it would join sentences), which is a documented residual.
    /// </summary>
    internal static string Deobfuscate(string lower)
    {
        var text = lower.Replace('\u3002', '.').Replace('\uFF61', '.').Replace('\uFF0E', '.');
        text = BracketedDotPattern.Replace(text, ".");
        return SpelledDotPattern.Replace(text, ".");
    }

    /// <summary>The links in <paramref name="text"/>, which the caller has folded; empty when there are none.</summary>
    internal static Links LinksIn(string text)
    {
        var deobfuscated = Deobfuscate(text.ToLowerInvariant());
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlPattern.Matches(deobfuscated))
        {
            urls.Add(TrimUrl(match.Value));
        }

        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DottedTokenPattern.Matches(deobfuscated))
        {
            if (AsHost(match.Value) is { } host)
            {
                hosts.Add(host);
            }
        }

        var ips = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in IpPattern.Matches(deobfuscated))
        {
            if (IsAddress(match.Value))
            {
                ips.Add(match.Value);
            }
        }

        return new Links(urls, hosts, ips, AlwaysRefusedLinkPattern.IsMatch(deobfuscated));
    }

    /// <summary>Whether <paramref name="text"/> holds any URL, hostname, IP address, colon-only scheme or UNC path.</summary>
    internal static bool HasLink(string text) => LinksIn(text) is var links
        && (links.AlwaysRefused || links.Urls.Count > 0 || links.Hosts.Count > 0 || links.Addresses.Count > 0);

    /// <summary>Whether <paramref name="text"/> holds credential-shaped text.</summary>
    internal static bool HasCredential(string text) => CredentialPattern.IsMatch(text);

    /// <summary>Whether <paramref name="text"/> holds instruction-override phrasing, in its phrase form or with its letters run together.</summary>
    internal static bool HasInstructionOverride(string text)
    {
        var phrase = PhraseForm(text);
        if (PhrasePattern.IsMatch(phrase))
        {
            return true;
        }

        var collapsed = phrase.Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var core in CollapsedPhrases)
        {
            if (collapsed.Contains(core, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return NewInstructionsPattern.IsMatch(CollapseWhitespace(text.ToLowerInvariant()));
    }

    /// <summary>Whether a word of <paramref name="text"/> mixes Latin letters with Cyrillic or Greek ones. The micro sign's Greek mu is neutral.</summary>
    internal static bool HasMixedScriptWord(string text)
    {
        bool latin = false, other = false;
        foreach (var c in text)
        {
            if (!char.IsLetter(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            {
                latin = other = false;
                continue;
            }

            if (IsLatin(c))
            {
                latin = true;
            }
            else if (IsCyrillicOrGreek(c))
            {
                other = true;
            }

            if (latin && other)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lower case, confusables folded, every run of characters that are not letters one space, trimmed.</summary>
    internal static string PhraseForm(string text)
    {
        var lower = text.ToLowerInvariant();
        var result = new StringBuilder(lower.Length);
        var space = false;
        foreach (var c in lower)
        {
            var mapped = Confusables.TryGetValue(c, out var latin) ? latin : c;
            if (char.IsLetter(mapped))
            {
                if (space && result.Length > 0)
                {
                    result.Append(' ');
                }

                space = false;
                result.Append(mapped);
            }
            else
            {
                space = true;
            }
        }

        return result.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && result.Length > 0)
            {
                result.Append(' ');
            }

            space = false;
            result.Append(c);
        }

        return result.ToString();
    }

    private static bool IsLatin(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
        or (>= '\u00C0' and <= '\u024F') or (>= '\u0250' and <= '\u02AF') or (>= '\u1E00' and <= '\u1EFF')
        or (>= '\u2C60' and <= '\u2C7F') or (>= '\uA720' and <= '\uA7FF') or (>= '\uAB30' and <= '\uAB6F');

    private static bool IsCyrillicOrGreek(char c) =>
        c != '\u03BC'
        && c is (>= '\u0370' and <= '\u03FF') or (>= '\u1F00' and <= '\u1FFF') or (>= '\u0400' and <= '\u052F')
            or (>= '\u1C80' and <= '\u1C8F') or (>= '\u2DE0' and <= '\u2DFF') or (>= '\uA640' and <= '\uA69F');

    /// <summary>A URL without the punctuation a sentence puts after it.</summary>
    private static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>');

    /// <summary>
    /// <paramref name="token"/> as a whole hostname, trimmed of dots and hyphens at either end, when its last label
    /// is a top-level domain: one of <see cref="Tlds"/>, an <c>xn--</c> label, or a label with a character outside
    /// ASCII (an internationalized TLD); otherwise <see langword="null"/>.
    /// </summary>
    private static string? AsHost(string token)
    {
        var host = token.Trim('.', '-');
        var dot = host.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var tld = host[(dot + 1)..].Trim('-');
        if (tld.Length == 0)
        {
            return null;
        }

        return Tlds.Contains(tld) || tld.StartsWith("xn--", StringComparison.Ordinal) || (tld.Any(c => c > '\u007F') && tld.Any(char.IsLetter))
            ? host
            : null;
    }

    /// <summary>A dotted quad whose four parts are each at most 255, or a hex form.</summary>
    private static bool IsAddress(string match) =>
        match.StartsWith("0x", StringComparison.Ordinal)
        || match.Split('.').All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) && octet <= 255);
}

/// <summary>The links found in one text: whole URLs, whole hostnames and IP addresses, lower-cased, and whether it holds a link that is always refused.</summary>
internal sealed record Links(
    IReadOnlySet<string> Urls,
    IReadOnlySet<string> Hosts,
    IReadOnlySet<string> Addresses,
    bool AlwaysRefused);
