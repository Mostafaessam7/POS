using System.Text.RegularExpressions;

namespace POS.ArchitectureTests;

/// <summary>
/// Rule 8 — no ambient time. Enforced by source scan rather than ArchUnit.
/// </summary>
/// <remarks>
/// ArchUnitNET reasons about type-level dependencies, and every file that touches
/// a date depends on <c>System.DateTime</c> legitimately. What we actually need to
/// forbid is a specific member access, so a source scan is both simpler and more
/// accurate than trying to express this structurally.
///
/// This is the highest-value rule in the set. It mechanically prevents the bug
/// class that silently corrupts daily reporting: a store trading past midnight
/// booking sales to the wrong business day because someone reached for
/// DateTime.Today instead of the shift's BusinessDate.
/// </remarks>
public sealed class AmbientTimeTests
{
    private static readonly Regex Forbidden = new(
        @"\b(DateTime|DateTimeOffset)\s*\.\s*(Now|UtcNow|Today)\b",
        RegexOptions.Compiled);

    /// <summary>Files permitted to read the system clock directly.</summary>
    private static readonly string[] AllowList =
    [
        // SystemClock — the one legitimate reader of the system clock — is declared
        // in IClock.cs, NOT in a file called SystemClock.cs. The original entry named
        // a file that does not exist, so the allow-list silently matched nothing.
        "IClock.cs",
        "AmbientTimeTests.cs"
    ];

    [Fact]
    public void No_production_code_should_read_the_system_clock_directly()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");

        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains("Migrations", StringComparison.Ordinal))
            .Where(path => !AllowList.Contains(Path.GetFileName(path)))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, number: index + 1))
                .Where(x => Forbidden.IsMatch(StripComments(x.line)))
                .Select(x => $"{Path.GetRelativePath(root, path)}:{x.number}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"""
             Direct system clock access found. Inject IClock instead.

             A POS has three distinct notions of "now" and conflating them produces
             reporting failures that surface weeks later:
               UtcNow        wall clock, for ordering in a trusted environment
               BusinessDate  the trading day, set at shift open — a store trading
                             until 02:00 books to the PREVIOUS day
               Terminal time an offline till's clock may be days out; display only

             Offenders:
             {string.Join(Environment.NewLine, offenders)}
             """);
    }

    /// <summary>
    /// Strips comments before matching, so documentation describing the forbidden
    /// pattern is not itself an offence.
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
