namespace POS.Catalog.Domain;

/// <summary>
/// Parses GS1-128 application identifiers out of a scanned string.
/// </summary>
/// <remarks>
/// Deli counters, butchers, and greengrocers print labels that encode the WEIGHT or
/// the PRICE inside the barcode. The scanner returns one string; the till must
/// resolve it to (SKU, quantity) or (SKU, price).
///
/// Designing for this after launch is painful, because it changes the signature of
/// every "scan" path — the scan no longer yields just an identifier, it yields an
/// identifier PLUS transaction data. Building it in now costs one parser.
///
/// Supported identifiers are deliberately a small closed set. Full GS1 has hundreds;
/// implementing them speculatively is waste.
/// </remarks>
public static class Gs1Parser
{
    /// <summary>Fixed-length AIs. Variable-length ones terminate at a group separator.</summary>
    private static readonly Dictionary<string, int> FixedLengths = new()
    {
        ["00"] = 18,  // SSCC
        ["01"] = 14,  // GTIN
        ["11"] = 6,   // production date  YYMMDD
        ["15"] = 6,   // best before      YYMMDD
        ["17"] = 6,   // expiry           YYMMDD
        ["3100"] = 6, // net weight, kg,  0 decimals
        ["3101"] = 6, // net weight, kg,  1 decimal
        ["3102"] = 6, // net weight, kg,  2 decimals
        ["3103"] = 6, // net weight, kg,  3 decimals
        ["3200"] = 6, // net weight, lb,  0 decimals
        ["3201"] = 6,
        ["3202"] = 6
    };

    private const char GroupSeparator = (char)29;

    public static Gs1Data Parse(string raw)
    {
        var data = new Gs1Data();
        var position = 0;

        while (position < raw.Length - 1)
        {
            var ai = ResolveApplicationIdentifier(raw, position);
            if (ai is null) break;

            position += ai.Length;

            string value;
            if (FixedLengths.TryGetValue(ai, out var length))
            {
                if (position + length > raw.Length) break;
                value = raw.Substring(position, length);
                position += length;
            }
            else
            {
                var end = raw.IndexOf(GroupSeparator, position);
                if (end < 0) end = raw.Length;
                value = raw[position..end];
                position = end + 1;
            }

            Assign(data, ai, value);
        }

        return data;
    }

    private static string? ResolveApplicationIdentifier(string raw, int position)
    {
        // Weight AIs are four characters (310n); most others are two.
        foreach (var length in stackalloc[] { 4, 2 })
        {
            if (position + length > raw.Length) continue;

            var candidate = raw.Substring(position, length);
            if (FixedLengths.ContainsKey(candidate) || (length == 2 && candidate is "10" or "21" or "30"))
                return candidate;
        }

        return null;
    }

    private static void Assign(Gs1Data data, string ai, string value)
    {
        switch (ai)
        {
            case "01": data.Gtin = value; break;
            case "10": data.BatchNumber = value; break;
            case "21": data.SerialNumber = value; break;
            case "17": data.ExpiryDate = ParseDate(value); break;
            case "30": data.Count = int.TryParse(value, out var c) ? c : null; break;

            // The trailing digit of a 310n AI is the decimal place count. 3102 with
            // "001250" means 12.50 kg.
            case "3100" or "3101" or "3102" or "3103":
                if (decimal.TryParse(value, out var weight))
                    data.NetWeightKg = weight / (decimal)Math.Pow(10, ai[3] - '0');
                break;
        }
    }

    private static DateOnly? ParseDate(string yymmdd)
    {
        if (yymmdd.Length != 6
            || !int.TryParse(yymmdd[..2], out var yy)
            || !int.TryParse(yymmdd[2..4], out var mm)
            || !int.TryParse(yymmdd[4..], out var dd))
            return null;

        // GS1 permits DD = 00, meaning "end of month".
        var year = 2000 + yy;
        if (dd == 0) dd = DateTime.DaysInMonth(year, mm);

        return mm is >= 1 and <= 12 ? new DateOnly(year, mm, dd) : null;
    }
}

public sealed class Gs1Data
{
    public string? Gtin { get; set; }
    public decimal? NetWeightKg { get; set; }
    public string? BatchNumber { get; set; }
    public string? SerialNumber { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public int? Count { get; set; }

    public bool HasEmbeddedQuantity => NetWeightKg.HasValue;
}
