namespace POS.SharedKernel;

/// <summary>
/// The only sanctioned source of the current time.
/// </summary>
/// <remarks>
/// Architecture rule 8 fails the build on any direct use of
/// <c>DateTime.Now</c> / <c>DateTime.UtcNow</c> / <c>DateTimeOffset.Now</c>
/// outside an implementation of this interface.
///
/// Two reasons, and the second is the one that costs money:
///
/// 1. Testability. Time-dependent logic is otherwise untestable without sleeping.
///
/// 2. Correctness. A POS has THREE distinct notions of "now" and conflating them
///    produces reporting failures that surface weeks later:
///      - UtcNow        — wall clock, for ordering within a trusted environment.
///      - BusinessDate  — the trading day, set at shift open. A store trading
///                        until 02:00 books those sales to the PREVIOUS day.
///                        Deriving this from DateTime.Today is always wrong.
///      - Terminal time — an offline till's clock may be days out. Display only;
///                        never use it for ordering. Use the terminal sequence.
/// </remarks>
public interface IClock
{
    public DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
