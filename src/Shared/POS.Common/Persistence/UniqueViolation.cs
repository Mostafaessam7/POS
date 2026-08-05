using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace POS.Common.Persistence;

/// <summary>
/// Recognises a unique-constraint violation from any provider this platform uses.
/// </summary>
/// <remarks>
/// THIS EXISTS BECAUSE THE OBVIOUS VERSION IS WRONG, and was wrong here for real.
/// The natural-looking check
///
///     ex.InnerException?.Message.Contains("2601")
///
/// never matches. 2601 and 2627 are SQL Server ERROR NUMBERS, not text that appears in
/// the message — the message reads "Cannot insert duplicate key row in object ... with
/// unique index ...". So every write that relied on that check to detect a benign
/// duplicate instead surfaced a 500, and the idempotency it was supposed to provide
/// existed only on paper. That is exactly the class of bug that hides until two
/// requests race.
///
/// The typed check below cannot drift the same way. SQLite is matched on message text
/// because the terminal agent's provider is not referenced here, and its wording
/// ("UNIQUE constraint failed") is stable and unlocalised.
/// </remarks>
public static class UniqueViolation
{
    /// <summary>SQL Server: 2627 is a unique constraint, 2601 a unique index.</summary>
    private const int UniqueConstraint = 2627;

    private const int UniqueIndex = 2601;

    public static bool Matches(DbUpdateException exception) => Matches((Exception?)exception);

    /// <summary>
    /// Recognises the violation whether it arrives wrapped or raw.
    /// </summary>
    /// <remarks>
    /// BOTH SHAPES OCCUR, and assuming one is a bug this codebase already shipped.
    /// <c>SaveChanges</c> wraps the provider exception in a <see cref="DbUpdateException"/>,
    /// but <c>ExecuteSqlInterpolatedAsync</c> does not — it lets the
    /// <see cref="SqlException"/> straight out. A <c>catch (DbUpdateException)</c> around
    /// raw SQL therefore never fires, which is exactly how the stock ledger's
    /// "correctness backstop" against a balance-row insert race sat dead until two
    /// concurrent uploads hit it.
    /// </remarks>
    public static bool Matches(Exception? exception) => exception switch
    {
        SqlException { Number: UniqueConstraint or UniqueIndex } => true,
        null => false,
        _ => exception.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)
             || Matches(exception.InnerException)
    };
}
