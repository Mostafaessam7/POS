using POS.SharedKernel;
using Shouldly;

namespace POS.UnitTests;

/// <summary>
/// The business date is the single most under-tested concept in POS systems and
/// the source of reconciliation failures that surface weeks after the fact.
/// </summary>
public sealed class BusinessDateTests
{
    [Fact]
    public void Sale_after_midnight_books_to_the_previous_trading_day()
    {
        // A bar closes at 02:00. The 01:30 sale belongs to Friday's takings, not
        // Saturday's — otherwise the Z-report does not match the drawer, and the
        // manager has an unexplained variance across two days.
        var oneThirtyAm = new DateTimeOffset(2026, 3, 14, 1, 30, 0, TimeSpan.Zero);

        BusinessDate.DeriveFrom(oneThirtyAm, dayStartHour: 4)
                    .Value
                    .ShouldBe(new DateOnly(2026, 3, 13));
    }

    [Fact]
    public void Sale_after_rollover_books_to_the_current_day()
    {
        var sixAm = new DateTimeOffset(2026, 3, 14, 6, 0, 0, TimeSpan.Zero);

        BusinessDate.DeriveFrom(sixAm, dayStartHour: 4)
                    .Value
                    .ShouldBe(new DateOnly(2026, 3, 14));
    }

    [Fact]
    public void Midnight_rollover_is_the_default_for_ordinary_retail()
    {
        var elevenPm = new DateTimeOffset(2026, 3, 14, 23, 0, 0, TimeSpan.Zero);

        BusinessDate.DeriveFrom(elevenPm, dayStartHour: 0)
                    .Value
                    .ShouldBe(new DateOnly(2026, 3, 14));
    }

    [Fact]
    public void Rejects_an_invalid_rollover_hour()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => BusinessDate.DeriveFrom(DateTimeOffset.UnixEpoch, dayStartHour: 24));
    }
}
