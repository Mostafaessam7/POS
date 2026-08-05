using System.Globalization;

namespace POS.SharedKernel;

/// <summary>
/// The trading day a transaction belongs to. NOT the calendar date.
/// </summary>
/// <remarks>
/// A store trading until 02:00 books those sales to the PREVIOUS business day.
/// Deriving this from <c>DateTime.Today</c> silently corrupts every daily report,
/// every cash reconciliation, and every Z-report — and the corruption is invisible
/// until someone tries to balance the drawer weeks later.
///
/// A business date is therefore ASSIGNED at shift open and carried on the
/// transaction. It is never computed at the point of sale. Architecture rule 8
/// fails the build on any direct system clock access precisely to prevent the
/// shortcut. See ADR 017.
/// </remarks>
public readonly record struct BusinessDate : IComparable<BusinessDate>
{
    private BusinessDate(DateOnly value) => Value = value;

    public DateOnly Value { get; }

    /// <summary>
    /// Opens a business date explicitly. The only construction path — there is
    /// deliberately no <c>BusinessDate.Today</c>.
    /// </summary>
    public static BusinessDate Open(DateOnly value) => new(value);

    /// <summary>
    /// Derives the business date for a moment in local store time, given the hour
    /// at which the store's trading day rolls over.
    /// </summary>
    /// <param name="localTime">The moment, already converted to store-local time.</param>
    /// <param name="dayStartHour">
    /// Hour (0–23) at which a new trading day begins. A 24-hour store rolling over
    /// at 04:00 passes 4: anything before 04:00 books to the previous day.
    /// </param>
    public static BusinessDate DeriveFrom(DateTimeOffset localTime, int dayStartHour)
    {
        if (dayStartHour is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(dayStartHour), "Must be 0-23.");

        var date = DateOnly.FromDateTime(localTime.DateTime);
        return new BusinessDate(localTime.Hour < dayStartHour ? date.AddDays(-1) : date);
    }

    public int CompareTo(BusinessDate other) => Value.CompareTo(other.Value);

    public static bool operator <(BusinessDate left, BusinessDate right) => left.CompareTo(right) < 0;
    public static bool operator <=(BusinessDate left, BusinessDate right) => left.CompareTo(right) <= 0;
    public static bool operator >(BusinessDate left, BusinessDate right) => left.CompareTo(right) > 0;
    public static bool operator >=(BusinessDate left, BusinessDate right) => left.CompareTo(right) >= 0;

    /// <remarks>
    /// Invariant culture is deliberate. A business date is a ledger key, not a
    /// display value: under a non-Gregorian calendar the current culture would
    /// format the same day differently, and that string reaches receipts, exports
    /// and reconciliation files.
    /// </remarks>
    public override string ToString() =>
        Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
