using System.Text.RegularExpressions;

namespace POS.ArchitectureTests;

/// <summary>
/// Rule 13 — no cardholder data anywhere in the codebase (ADR 045).
/// </summary>
/// <remarks>
/// <para>
/// PCI-DSS scope is a step function, not a gradient. A system that never touches a
/// primary account number qualifies for SAQ P2PE — a short self-assessment. A system
/// that touches one, once, anywhere, needs a full Report on Compliance: an audited
/// annual engagement costing tens of thousands, applied to every customer of the
/// platform.
/// </para>
/// <para>
/// The distance between those two worlds is one well-meaning field added by someone who
/// wanted to show the last four digits and had the whole PAN to hand. That change would
/// pass code review — it looks like a display concern. This test is the control that
/// catches it, and it fails the build rather than warning, because a warning about
/// compliance scope is a warning nobody reads.
/// </para>
/// <para>
/// Storage is not the trigger. Under PCI-DSS the PAN is in scope the moment it is
/// <b>processed or transmitted</b>, so a field that merely passes one through is enough.
/// That is why this scans for the concept, not for database columns.
/// </para>
/// </remarks>
public sealed class CardDataArchitectureTests
{
    /// <summary>
    /// Identifiers that indicate cardholder data is being modelled.
    /// </summary>
    /// <remarks>
    /// <c>MaskedPan</c> is deliberately absent: the last four digits are explicitly not
    /// cardholder data under PCI-DSS and are needed to match a receipt to a statement.
    /// The regex below requires a word boundary so <c>MaskedPan</c> does not trip the
    /// <c>Pan</c> rule.
    /// </remarks>
    private static readonly string[] ForbiddenIdentifiers =
    [
        "CardNumber",
        "CardholderName",
        "Cvv",
        "Cvc",
        "SecurityCode",
        "TrackData",
        "Track1",
        "Track2",
        "ExpiryMonth",
        "ExpiryYear",
        "ExpirationDate",
        "PinBlock",
        "MagStripe",
    ];

    private static readonly Regex BarePan = new(
        @"(?<![A-Za-z])(?:Full)?Pan\b(?!\w)",
        RegexOptions.Compiled);

    /// <summary>
    /// Candidate card-number literals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious rule — any 13-to-19 digit literal — is wrong in <b>this</b> domain,
    /// and the first run proved it by flagging <c>BarcodeTests</c>. An EAN-13 barcode is
    /// thirteen digits, an ITF-14 is fourteen, and a retail codebase is full of both.
    /// A check that cries wolf on legitimate product codes gets suppressed within a
    /// week, and then it is protecting nothing.
    /// </para>
    /// <para>
    /// So the rule is narrowed on two axes: at least fourteen digits, which excludes
    /// EAN-13 outright, and a passing Luhn checksum, which every card number satisfies
    /// by definition and an arbitrary product code satisfies only by luck. False
    /// positives remain possible; <see cref="KnownNonCardLiterals"/> is the escape
    /// hatch, and it requires writing down what the number actually is.
    /// </para>
    /// </remarks>
    private static readonly Regex DigitLiteral = new(
        @"""(?<digits>[0-9]{14,19})""",
        RegexOptions.Compiled);

    /// <summary>Long numeric literals that are demonstrably not card numbers.</summary>
    private static readonly string[] KnownNonCardLiterals = [];

    [Fact]
    public void No_source_file_models_cardholder_data()
    {
        var offenders = new List<string>();

        foreach (var (path, text) in SourceFiles())
        {
            var code = StripCommentsAndDocs(text);

            foreach (var identifier in ForbiddenIdentifiers)
            {
                if (Regex.IsMatch(code, $@"\b{Regex.Escape(identifier)}\b"))
                {
                    offenders.Add($"{path}: {identifier}");
                }
            }

            if (BarePan.IsMatch(code))
            {
                offenders.Add($"{path}: bare PAN identifier");
            }
        }

        offenders.ShouldBeEmpty(
            "cardholder data must never be modelled, processed or transmitted by this " +
            "application. The card is read and encrypted inside a P2PE-certified device " +
            "and we handle only the opaque payload and the last four digits. Introducing " +
            "any of these moves every customer of this platform from SAQ P2PE to a full " +
            "Report on Compliance — see ADR 045.");
    }

    [Fact]
    public void No_source_file_contains_a_card_number_literal()
    {
        var offenders = new List<string>();

        foreach (var (path, text) in SourceFiles())
        {
            var code = StripCommentsAndDocs(text);

            foreach (Match match in DigitLiteral.Matches(code))
            {
                var digits = match.Groups["digits"].Value;

                if (KnownNonCardLiterals.Contains(digits, StringComparer.Ordinal))
                {
                    continue;
                }

                if (PassesLuhn(digits))
                {
                    offenders.Add($"{path}: {digits.Length}-digit Luhn-valid literal");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "a long digit literal looks like a test card number. Even a well-known test " +
            "PAN must not appear in source: it trains people that card numbers in the " +
            "repository are normal, and it is the first thing a scanner flags.");
    }

    /// <summary>
    /// The card reader's contract must expose nothing but the encrypted payload and the
    /// masked value.
    /// </summary>
    [Fact]
    public void The_card_read_result_exposes_only_non_sensitive_fields()
    {
        var file = SourceFiles()
            .FirstOrDefault(f => f.Path.EndsWith("HardwareContracts.cs", StringComparison.Ordinal));

        file.Text.ShouldNotBeNull("the hardware contracts file should exist");

        var record = Regex.Match(
            file.Text,
            @"record CardReadResult\((?<body>[^)]*)\)",
            RegexOptions.Singleline);

        record.Success.ShouldBeTrue("CardReadResult should be declared as a positional record");

        var body = record.Groups["body"].Value;

        body.Contains("EncryptedPayload", StringComparison.Ordinal).ShouldBeTrue();
        body.Contains("MaskedPan", StringComparison.Ordinal).ShouldBeTrue();
        body.Contains("Cvv", StringComparison.Ordinal).ShouldBeFalse();
        body.Contains("Expiry", StringComparison.Ordinal).ShouldBeFalse();
    }

    /// <summary>
    /// The checksum every payment card number satisfies (ISO/IEC 7812).
    /// </summary>
    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubling = false;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var value = digits[i] - '0';

            if (doubling)
            {
                value *= 2;

                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            doubling = !doubling;
        }

        return sum % 10 == 0;
    }

    private static List<(string Path, string Text)> SourceFiles()
    {
        var root = SolutionRoot();
        var results = new List<(string, string)>();

        foreach (var dir in new[] { "src", "tests" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetFileName(file) == "CardDataArchitectureTests.cs")
                {
                    continue;
                }

                results.Add((Path.GetRelativePath(root, file), File.ReadAllText(file)));
            }
        }

        return results;
    }

    /// <summary>
    /// Strips comments so that prose explaining why we do NOT hold card data is not
    /// itself reported as holding card data.
    /// </summary>
    /// <remarks>
    /// The same defect appeared in <c>AmbientTimeTests</c>: a scanner that reads
    /// documentation as code punishes the act of documenting the rule.
    /// </remarks>
    private static string StripCommentsAndDocs(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", string.Empty, RegexOptions.Multiline);
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
