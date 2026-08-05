using System.Text.RegularExpressions;

namespace POS.ArchitectureTests;

/// <summary>
/// Rule 11 — the core domain contains no jurisdiction knowledge (ADR 031).
/// </summary>
/// <remarks>
/// <para>
/// The entire value of the pluggable fiscal design rests on one property: no country
/// logic leaks into the core. That property is easy to state, easy to agree with in
/// review, and extremely easy to violate under delivery pressure — the first "just
/// this once" special case for a launch customer is how every plugin architecture
/// dies.
/// </para>
/// <para>
/// So it is enforced mechanically rather than by discipline. A source scan is the
/// right tool: what we are forbidding is a string literal and a naming pattern, not a
/// type dependency ArchUnit could see.
/// </para>
/// <para>
/// If a legitimate need arises, the fix is a new capability flag or a new seam. It is
/// never a country check in the core, and this test is what forces that conversation
/// to happen.
/// </para>
/// </remarks>
public sealed class FiscalAgnosticismTests
{
    /// <summary>Regimes, authorities, and formats that must only appear inside plugins.</summary>
    private static readonly Regex JurisdictionTerms = new(
        @"\b(ZATCA|FatturaPA|CFDI|KSeF|SAF-?T|JPK|ATCUD|NF-?e|PEPPOL|SdI)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ISO country literals, e.g. <c>== "SA"</c> or <c>Equals("EG")</c>.</summary>
    private static readonly Regex CountryComparison = new(
        @"(==|!=|Equals\s*\(|Contains\s*\(|case)\s*""[A-Z]{2}""",
        RegexOptions.Compiled);

    /// <summary>
    /// Directories that ARE allowed to name jurisdictions: the country plugins
    /// themselves, and nothing else.
    /// </summary>
    private static readonly string[] PluginPathFragments =
    [
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Sa"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Eg"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Ae"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.It"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Pt"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Pl"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Mx"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Br"),
        Path.Combine("Modules", "Fiscal", "POS.Fiscal.Cl")
    ];

    [Fact]
    public void No_jurisdiction_specific_terms_appear_outside_country_plugins()
    {
        var offenders = ScanSource((file, line, text) =>
            JurisdictionTerms.IsMatch(StripComments(text))
                ? $"{file}:{line} — {text.Trim()}"
                : null);

        offenders.ShouldBeEmpty(
            "jurisdiction-specific terms belong in a country plugin. " +
            "If the core needs to behave differently, add a FiscalCapabilities flag " +
            "or a new seam — see ADR 031.");
    }

    [Fact]
    public void No_country_code_comparisons_appear_outside_country_plugins()
    {
        var offenders = ScanSource((file, line, text) =>
            CountryComparison.IsMatch(StripComments(text))
                ? $"{file}:{line} — {text.Trim()}"
                : null);

        offenders.ShouldBeEmpty(
            "a country-code comparison in the core is the defect ADR 031 exists to " +
            "prevent. Express the difference as a capability instead.");
    }

    [Fact]
    public void Country_plugins_depend_only_on_the_abstractions_assembly()
    {
        // Plugins must not reach into the pipeline or another plugin. Enforcing the
        // dependency direction is what keeps jurisdictions independently releasable.
        var root = SolutionRoot();
        var pluginDirs = Directory.Exists(Path.Combine(root, "src", "Modules", "Fiscal"))
            ? Directory.GetDirectories(Path.Combine(root, "src", "Modules", "Fiscal"))
                       .Where(d => PluginPathFragments.Any(f => d.Contains(f, StringComparison.OrdinalIgnoreCase))
                                   || Path.GetFileName(d).Equals("POS.Fiscal.Generic", StringComparison.OrdinalIgnoreCase))
            : [];

        var offenders = new List<string>();

        foreach (var dir in pluginDirs)
        {
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (var (line, index) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
                {
                    if (line.StartsWith("using POS.Fiscal.Pipeline", StringComparison.Ordinal))
                    {
                        offenders.Add($"{file}:{index} — plugin depends on the pipeline");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty("plugins depend on POS.Fiscal.Abstractions only.");
    }

    private static List<string> ScanSource(Func<string, int, string, string?> inspect)
    {
        var root = SolutionRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (PluginPathFragments.Any(f => file.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var (line, index) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (inspect(file, index, line) is { } offence)
                {
                    offenders.Add(offence);
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// Documentation legitimately names jurisdictions to explain WHY a seam exists;
    /// only executable code is constrained.
    /// </summary>
    private static string StripComments(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('*')
            || trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the solution root.");
    }
}
