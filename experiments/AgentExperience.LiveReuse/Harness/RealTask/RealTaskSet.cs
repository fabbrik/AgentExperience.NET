using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>One of the six upgrade breakages: the feature it breaks and the file where it is fixed.</summary>
/// <param name="Feature">The feature package under <c>app/</c> the breakage lives in.</param>
/// <param name="FixPath">The one file every service of the cluster needs edited, relative to the repository root.</param>
/// <param name="Library">The changed function of the vendored library.</param>
public sealed record RealCluster(string Feature, string FixPath, string Library);

/// <summary>One service of the task set.</summary>
/// <param name="Index">0 to 11, in cluster order.</param>
/// <param name="Service">The service name (the repository's name and the first word of its ticket).</param>
/// <param name="Cluster">The feature whose upgrade broke it.</param>
/// <param name="Variant">0 or 1: which of the cluster's two services this is.</param>
public sealed record RealService(int Index, string Service, string Cluster, int Variant);

/// <summary>
/// The real-task experiment's task set (story 24.1): twelve small Python services that each pin a vendored library,
/// <c>quirklib</c>, whose 2.0 release changed six behaviours. Each service is broken by exactly one of them, so its
/// own unit tests fail until the consumer of that function is adapted. The fix lives in the same file for both
/// services of a cluster and in a different file for every other cluster.
/// </summary>
/// <remarks>
/// realtask@2 (pre-registration amendment 3) pads each repository to a realistic size: a deprecated twin of every
/// cluster's package that holds the same broken code and is imported by nothing, and twelve unrelated extension packages.
/// <para>
/// Nothing an agent reads names the file to edit. The failing test and its traceback say which feature is wrong, the
/// vendored changelog lists all six changes, and the code that consumes the changed function is two hops from the facade
/// the tests call, among five healthy features. Finding it costs steps; that cost is what experience is meant to cut.
/// The repository is generated at run time into a temporary directory from this class's text, so nothing here is
/// downloaded and nothing needs a network.
/// </para>
/// </remarks>
public sealed class RealTaskSet
{
    public const string Version = "realtask@2";

    /// <summary>The six clusters, in the order the services list them.</summary>
    public static IReadOnlyList<RealCluster> Clusters { get; } =
    [
        new("pricing", "app/pricing/helpers.py", "parse_amount"),
        new("catalog", "app/catalog/helpers.py", "slugify"),
        new("proration", "app/proration/helpers.py", "days_between"),
        new("batching", "app/batching/helpers.py", "chunk"),
        new("accounts", "app/accounts/helpers.py", "normalize_email"),
        new("reports", "app/reports/helpers.py", "percent"),
    ];

    private static readonly string[] ServiceNames =
    [
        "shopcart", "invoicer",
        "storefront", "wishlist",
        "seatbilling", "rentmeter",
        "mailqueue", "exportjobs",
        "signupflow", "crmsync",
        "auditboard", "slatracker",
    ];

    /// <summary>The current task set.</summary>
    public static RealTaskSet Current { get; } = new();

    private RealTaskSet()
    {
        Services = [.. ServiceNames.Select((name, index) => new RealService(index, name, Clusters[index / 2].Feature, index % 2))];
    }

    /// <summary>The twelve services.</summary>
    public IReadOnlyList<RealService> Services { get; }

    /// <summary>The cluster of <paramref name="service"/>.</summary>
    public static RealCluster ClusterOf(RealService service) => Clusters.Single(cluster => cluster.Feature == service.Cluster);

    /// <summary>The other service of <paramref name="service"/>'s cluster.</summary>
    public RealService Sibling(RealService service) => Services.Single(other => other.Cluster == service.Cluster && other.Index != service.Index);

    /// <summary>A digest over every generated file of every service and the reference fixes: a changed task set changes it.</summary>
    public string Digest()
    {
        var text = new StringBuilder();
        foreach (var service in Services)
        {
            foreach (var (path, content) in Files(service).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                text.Append(service.Service).Append('|').Append(path).Append('|').Append(content).Append('\n');
            }

            text.Append(service.Service).Append("|ticket|").Append(Ticket(service)).Append('\n');
        }

        foreach (var cluster in Clusters)
        {
            text.Append("fix|").Append(cluster.Feature).Append('|').Append(FixedHelpers(cluster.Feature)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The ticket text the agent is given: the symptom as a user would report it, never the file to edit.</summary>
    public static string Ticket(RealService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.Cluster switch
        {
            "pricing" => service.Variant == 0
                ? $"`{service.Service}`: checkout totals are wrong since the quirklib 2.0 upgrade. A basket of 12.50 twice plus 3.00 once should total 28.0 but the total comes out far too large. Fix the service so its tests pass."
                : $"`{service.Service}`: the price total on an invoice is wrong since the quirklib 2.0 upgrade. Four items at 7.25 and two at 0.50 should total 30.0 but the total is far too large. Fix the service so its tests pass.",
            "catalog" => service.Variant == 0
                ? $"`{service.Service}`: product page URLs are wrong since the quirklib 2.0 upgrade. The product 'Blue Widget' should live at /p/blue-widget but the URL has the wrong separators. Fix the service so its tests pass."
                : $"`{service.Service}`: product links are wrong since the quirklib 2.0 upgrade. 'Oak Desk (Large)' should link to /p/oak-desk-large but the URL has the wrong separators. Fix the service so its tests pass.",
            "proration" => service.Variant == 0
                ? $"`{service.Service}`: the prorated charge is too low since the quirklib 2.0 upgrade. Ten days of a 300.0 plan in a 30 day month should cost 100.0. Fix the service so its tests pass."
                : $"`{service.Service}`: the prorated charge for a part month is too low since the quirklib 2.0 upgrade. Ten days of a 620.0 plan in a 31 day month should cost 200.0. Fix the service so its tests pass.",
            "batching" => service.Variant == 0
                ? $"`{service.Service}`: counting the batches fails since the quirklib 2.0 upgrade. Ten items in batches of three should count 4 batches, but the count raises an error. Fix the service so its tests pass."
                : $"`{service.Service}`: the batch count crashes since the quirklib 2.0 upgrade. Twenty-five items in batches of ten should count 3 batches, but the count raises an error. Fix the service so its tests pass.",
            "accounts" => service.Variant == 0
                ? $"`{service.Service}`: duplicate accounts are not merged since the quirklib 2.0 upgrade. ' Ann@Example.com' and 'ann@example.com' are one account but are listed twice. Fix the service so its tests pass."
                : $"`{service.Service}`: the unique account list has duplicates since the quirklib 2.0 upgrade. 'Zed@Mail.io' and 'zed@mail.io ' are one account but are listed twice. Fix the service so its tests pass.",
            "reports" => service.Variant == 0
                ? $"`{service.Service}`: the completion rate shown is wrong since the quirklib 2.0 upgrade. One of two done should read 50% but it reads 0%. Fix the service so its tests pass."
                : $"`{service.Service}`: the completion percentage is wrong since the quirklib 2.0 upgrade. Three of four done should read 75% but it reads 1%. Fix the service so its tests pass.",
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };
    }

    /// <summary>Every file of <paramref name="service"/>'s repository as shipped (broken), keyed by relative path.</summary>
    public Dictionary<string, string> Files(RealService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = $"# {service.Service}\n\nA small service. Run its tests with `python3 -m unittest discover -s tests -t .`.\n"
                + "It depends on the vendored library in `vendor/` (read-only).\n",
            ["app/__init__.py"] = string.Empty,
            ["tests/__init__.py"] = string.Empty,
            ["vendor/__init__.py"] = string.Empty,
            ["vendor/quirklib.py"] = Quirklib,
            ["vendor/CHANGELOG.md"] = Changelog,
            ["app/service.py"] = Facade,
            ["tests/test_service.py"] = Tests(service),
        };

        foreach (var cluster in Clusters)
        {
            files["app/" + cluster.Feature + "/__init__.py"] = string.Empty;
            files["app/" + cluster.Feature + "/api.py"] = Api(cluster.Feature);
            files[cluster.FixPath] = cluster.Feature == service.Cluster ? BrokenHelpers(cluster.Feature) : FixedHelpers(cluster.Feature);
        }

        // realtask@2: a repository of realistic size. Each cluster has a deprecated twin that holds the same broken code and is
        // not imported by anything, and twelve unrelated feature packages sit beside them, so finding the file to edit costs steps.
        foreach (var cluster in Clusters)
        {
            files["app/" + cluster.Feature + "_legacy/__init__.py"] = string.Empty;
            files["app/" + cluster.Feature + "_legacy/api.py"] = "\"\"\"Deprecated copy kept for the 1.x migration script. Nothing in the service imports it.\"\"\"\n\n" + Api(cluster.Feature).Replace("app." + cluster.Feature + " ", "app." + cluster.Feature + "_legacy ", StringComparison.Ordinal);
            files["app/" + cluster.Feature + "_legacy/helpers.py"] = BrokenHelpers(cluster.Feature).Replace("Helpers for this feature.", "Deprecated copy of this feature's helpers, kept for the 1.x migration script.", StringComparison.Ordinal);
        }

        for (var index = 0; index < ExtensionNames.Length; index++)
        {
            var name = ExtensionNames[index];
            var folder = "app/ext_" + name + "/";
            files[folder + "__init__.py"] = string.Empty;
            files[folder + "api.py"] = $"from app.ext_{name} import helpers\n\n\ndef run(value):\n    return helpers.normalize(value)\n";
            files[folder + "helpers.py"] = $"\"\"\"Helpers for the {name} extension.\"\"\"\n\nfrom vendor import quirklib\n\nLIMIT = {10 + (index * 7)}\n\n\ndef normalize(value):\n    return quirklib.clamp(len(str(value)), 0, LIMIT)\n";
            files[folder + "models.py"] = $"from dataclasses import dataclass\n\n\n@dataclass\nclass {char.ToUpperInvariant(name[0])}{name[1..]}Entry:\n    key: str\n    weight: int = {index + 1}\n";
        }

        return files;
    }

    private static readonly string[] ExtensionNames =
    [
        "audit", "cache", "config", "currency", "dates", "feeds", "flags", "hooks", "limits", "mailer", "metrics", "tags",
    ];

    /// <summary>Writes <paramref name="service"/>'s repository into <paramref name="directory"/>.</summary>
    public void Materialize(RealService service, string directory)
    {
        foreach (var (path, content) in Files(service))
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }
    }

    /// <summary>The helper file of <paramref name="feature"/> already adapted to quirklib 2.0 (the reference fix).</summary>
    public static string FixedHelpers(string feature) => feature switch
    {
        "pricing" => Helpers("def line_total(price_text, qty):\n    return quirklib.parse_amount(price_text) * qty / 100\n"),
        "catalog" => Helpers("def slug(name):\n    return quirklib.slugify(name).replace(\"_\", \"-\")\n"),
        "proration" => Helpers("def billable_days(start, end):\n    return quirklib.days_between(start, end) + 1\n"),
        "batching" => Helpers("def split(items, size):\n    return list(quirklib.chunk(items, size))\n"),
        "accounts" => Helpers("def canonical(email):\n    return quirklib.normalize_email(email).lower()\n"),
        "reports" => Helpers("def pct(done, total):\n    return quirklib.percent(done, total) * 100\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    private static string BrokenHelpers(string feature) => feature switch
    {
        "pricing" => Helpers("def line_total(price_text, qty):\n    return quirklib.parse_amount(price_text) * qty\n"),
        "catalog" => Helpers("def slug(name):\n    return quirklib.slugify(name)\n"),
        "proration" => Helpers("def billable_days(start, end):\n    return quirklib.days_between(start, end)\n"),
        "batching" => Helpers("def split(items, size):\n    return quirklib.chunk(items, size)\n"),
        "accounts" => Helpers("def canonical(email):\n    return quirklib.normalize_email(email)\n"),
        "reports" => Helpers("def pct(done, total):\n    return quirklib.percent(done, total)\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    private static string Helpers(string body) =>
        "\"\"\"Helpers for this feature. Depends on the vendored quirklib.\"\"\"\n\nfrom vendor import quirklib\n\n\n" + body;

    private static string Api(string feature) => feature switch
    {
        "pricing" => "from app.pricing import helpers\n\n\ndef total(items):\n    return round(sum(helpers.line_total(price, qty) for price, qty in items), 2)\n",
        "catalog" => "from app.catalog import helpers\n\n\ndef product_url(name):\n    return \"/p/\" + helpers.slug(name)\n",
        "proration" => "from app.proration import helpers\n\n\ndef prorate(monthly, start, end, month_days):\n    return round(monthly * helpers.billable_days(start, end) / month_days, 2)\n",
        "batching" => "from app.batching import helpers\n\n\ndef count(items, size):\n    return len(helpers.split(items, size))\n",
        "accounts" => "from app.accounts import helpers\n\n\ndef unique(emails):\n    return sorted({helpers.canonical(email) for email in emails})\n",
        "reports" => "from app.reports import helpers\n\n\ndef label(done, total):\n    return \"%d%%\" % round(helpers.pct(done, total))\n",
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    private const string Facade = """"
        """Public functions of the service. The tests call these."""

        from app.accounts import api as _accounts
        from app.batching import api as _batching
        from app.catalog import api as _catalog
        from app.pricing import api as _pricing
        from app.proration import api as _proration
        from app.reports import api as _reports


        def checkout_total(items):
            return _pricing.total(items)


        def product_url(name):
            return _catalog.product_url(name)


        def prorated_charge(monthly, start, end, month_days):
            return _proration.prorate(monthly, start, end, month_days)


        def batch_count(items, size):
            return _batching.count(items, size)


        def unique_accounts(emails):
            return _accounts.unique(emails)


        def completion_label(done, total):
            return _reports.label(done, total)
        """";

    private const string Quirklib = """"
        """quirklib 2.0.0 (vendored, read-only)."""

        import re


        def parse_amount(text):
            whole, _, frac = text.strip().partition(".")
            return int(whole) * 100 + int((frac + "00")[:2])


        def slugify(text):
            return "_".join(part for part in re.split(r"[^a-z0-9]+", text.lower()) if part)


        def days_between(start, end):
            return (end - start).days


        def chunk(items, size):
            return (items[i:i + size] for i in range(0, len(items), size))


        def normalize_email(email):
            return email.strip()


        def percent(part, total):
            return part / total


        def clamp(value, low, high):
            return max(low, min(high, value))
        """";

    private const string Changelog = """
        # quirklib changelog

        ## 2.0.0

        - `parse_amount(text)` returns whole cents as an int (it returned dollars as a float).
        - `slugify(text)` joins words with underscores (it joined them with hyphens).
        - `days_between(start, end)` no longer counts the end day (it counted both days).
        - `chunk(items, size)` returns a generator (it returned a list).
        - `normalize_email(email)` only strips whitespace (it also lowercased).
        - `percent(part, total)` returns a fraction between 0 and 1 (it returned 0 to 100).

        ## 1.9.0

        - `clamp(value, low, high)` added.
        """;

    private static string Tests(RealService service)
    {
        var v = service.Variant;
        var checkout = v == 0 ? "[(\"12.50\", 2), (\"3.00\", 1)]" : "[(\"7.25\", 4), (\"0.50\", 2)]";
        var checkoutWant = v == 0 ? "28.0" : "30.0";
        var product = v == 0 ? "Blue Widget" : "Oak Desk (Large)";
        var url = v == 0 ? "/p/blue-widget" : "/p/oak-desk-large";
        var prorate = v == 0 ? "300.0, date(2026, 3, 1), date(2026, 3, 10), 30" : "620.0, date(2026, 5, 11), date(2026, 5, 20), 31";
        var prorateWant = v == 0 ? "100.0" : "200.0";
        var batchArgs = v == 0 ? "list(range(10)), 3" : "list(range(25)), 10";
        var batchWant = v == 0 ? "4" : "3";
        var emails = v == 0 ? "[\" Ann@Example.com\", \"ann@example.com\", \"Bob@Example.com\"]" : "[\"Zed@Mail.io\", \"zed@mail.io \", \"amy@mail.io\"]";
        var emailsWant = v == 0 ? "[\"ann@example.com\", \"bob@example.com\"]" : "[\"amy@mail.io\", \"zed@mail.io\"]";
        var done = v == 0 ? "1, 2" : "3, 4";
        var doneWant = v == 0 ? "50%" : "75%";

        return $"""
            import unittest
            from datetime import date

            from app import service


            class ServiceTests(unittest.TestCase):
                def test_checkout_total(self):
                    self.assertEqual(service.checkout_total({checkout}), {checkoutWant})

                def test_product_url(self):
                    self.assertEqual(service.product_url("{product}"), "{url}")

                def test_prorated_charge(self):
                    self.assertEqual(service.prorated_charge({prorate}), {prorateWant})

                def test_batch_count(self):
                    self.assertEqual(service.batch_count({batchArgs}), {batchWant})

                def test_unique_accounts(self):
                    self.assertEqual(service.unique_accounts({emails}), {emailsWant})

                def test_completion_label(self):
                    self.assertEqual(service.completion_label({done}), "{doneWant}")


            if __name__ == "__main__":
                unittest.main()
            """;
    }
}
