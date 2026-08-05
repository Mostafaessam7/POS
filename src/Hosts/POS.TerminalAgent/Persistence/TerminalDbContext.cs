using Microsoft.EntityFrameworkCore;
using POS.Common.Outbox;

namespace POS.TerminalAgent.Persistence;

/// <summary>
/// The terminal's local SQLite store.
/// </summary>
/// <remarks>
/// This is the durable record of a sale until the server acknowledges it. It is
/// backed up, it survives a power cut, and it is deliberately NOT browser storage.
///
/// WAL mode is mandatory. Without it, the sync worker writing while the till UI
/// reads produces SQLITE_BUSY under exactly the conditions where it hurts most —
/// a queue of customers and a background upload running simultaneously.
/// </remarks>
public sealed class TerminalDbContext(DbContextOptions<TerminalDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("Outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // The drain query: pending, due, oldest first.
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.OccurredAt })
                  .HasDatabaseName("IX_Outbox_Drain");
        });
    }

    public static void ConfigureConnection(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // WAL: concurrent reader while the sync worker writes.
        // NORMAL synchronous: durable across process crash, which is what we need;
        //   FULL additionally survives OS crash at a significant write cost.
        // busy_timeout: wait rather than throw on a momentarily locked database.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            """;

        command.ExecuteNonQuery();
    }
}
