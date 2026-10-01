using System.Text;
using System.Text.RegularExpressions;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Reflections;

/// <summary>
/// The content guard finalization's screening applies to a model-authored reflection (any
/// <see cref="Reflection.Authorship"/> other than <see cref="ReflectionAuthorship.Deterministic"/>), after hygiene
/// and the host sanitizer: a fixed, deterministic, best-effort filter. It never calls a model and is never applied
/// to a deterministic reflection.
/// </summary>
/// <remarks>
/// <para>
/// Every field is folded first: the screening's invisible-character removal, Unicode NFKC normalization, and the
/// removal again. It then refuses credential-shaped text, a word mixing Latin with Cyrillic or Greek letters,
/// instruction-override phrasing (in each field and across all of them joined), a <c>data:</c>,
/// <c>javascript:</c>, <c>vbscript:</c> or <c>file:</c> link or a UNC path, and any whole URL, hostname or IP
/// address that is not, as a whole token, among those in the run content the reflector was given (see
/// <see cref="IReflectionRunContent"/>). With no run content, every URL, hostname and IP address is refused.
/// </para>
/// <para>
/// It is a filter, not a boundary: content echoed from the run, a poisoned tool result included, passes by design,
/// and paraphrase passes too. The label and <c>ModelAuthoredLessons = Exclude</c> at injection, and the approval
/// boundary around tools, are the controls.
/// </para>
/// </remarks>
internal static class ModelAuthoredContentGuard
{
    /// <summary>
    /// The run content a reflector that does not implement <see cref="IReflectionRunContent"/> is taken to have
    /// been given: the task text (or the task ID when there is none), each tool name, each tool call's and each
    /// attempt's result and error, and the check IDs, in full.
    /// </summary>
    internal static IReadOnlyList<string> DefaultRunContent(ReflectionRequest request)
    {
        var content = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                content.Add(value);
            }
        }

        var run = request.Run;
        Add(run.TaskDescription ?? run.TaskId);
        foreach (var attempt in run.Attempts ?? [])
        {
            foreach (var call in attempt?.ToolCalls ?? [])
            {
                Add(call?.ToolName);
                Add(call?.Result);
                Add(call?.Error);
            }

            Add(attempt?.Result);
            Add(attempt?.Error);
        }

        foreach (var evidence in request.Evaluation.Outcome?.Evidence ?? [])
        {
            Add(evidence?.CheckId);
        }

        return content;
    }

    /// <summary>The whole URLs, hostnames and IP addresses in <paramref name="content"/>; none when it is <see langword="null"/>.</summary>
    internal static Links LinksOf(IReadOnlyList<string>? content)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in content ?? [])
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            try
            {
                var links = ModelTextPatterns.LinksIn(Fold(text));
                urls.UnionWith(links.Urls);
                hosts.UnionWith(links.Hosts);
                addresses.UnionWith(links.Addresses);
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // Content that cannot be read admits nothing: the guard only gets stricter.
            }
        }

        return new Links(urls, hosts, addresses, AlwaysRefused: false);
    }

    /// <summary>
    /// The rule <paramref name="value"/> breaks, as a content-free phrase, or <see langword="null"/> when it breaks
    /// none. The matched text is never returned.
    /// </summary>
    internal static string? Check(string value, Links run)
    {
        try
        {
            var folded = Fold(value);
            if (ModelTextPatterns.HasCredential(folded))
            {
                return "credential-shaped text";
            }

            if (ModelTextPatterns.HasMixedScriptWord(folded))
            {
                return "a word mixing Latin with Cyrillic or Greek letters";
            }

            if (ModelTextPatterns.HasInstructionOverride(folded))
            {
                return "instruction-override phrasing";
            }

            var links = ModelTextPatterns.LinksIn(folded);
            if (links.AlwaysRefused)
            {
                return "a data:, javascript:, vbscript: or file: link or a UNC path";
            }

            if (!links.Urls.IsSubsetOf(run.Urls))
            {
                return "a URL that is not in the captured run";
            }

            if (!links.Hosts.IsSubsetOf(run.Hosts))
            {
                return "a hostname that is not in the captured run";
            }

            if (!links.Addresses.IsSubsetOf(run.Addresses))
            {
                return "an IP address that is not in the captured run";
            }

            return null;
        }
        catch (ArgumentException)
        {
            return "text that cannot be normalized";
        }
        catch (RegexMatchTimeoutException)
        {
            return "text that could not be checked in time";
        }
    }

    /// <summary>Instruction-override phrasing in all of <paramref name="fields"/> joined by spaces, so a phrase split across items is caught.</summary>
    internal static string? CheckCombined(IEnumerable<string?> fields)
    {
        try
        {
            var joined = Fold(string.Join(' ', fields.Where(field => field is not null)));
            return ModelTextPatterns.HasInstructionOverride(joined) ? "instruction-override phrasing" : null;
        }
        catch (ArgumentException)
        {
            return "text that cannot be normalized";
        }
        catch (RegexMatchTimeoutException)
        {
            return "text that could not be checked in time";
        }
    }

    /// <summary>Invisible characters removed, NFKC-normalized, invisible characters removed again.</summary>
    internal static string Fold(string value) =>
        ReflectionScreening.Neutralize(ReflectionScreening.Neutralize(value).Normalize(NormalizationForm.FormKC));
}
