using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Payments.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAttemptNumberNotIdentity : Migration
    {
        // HAND-WRITTEN. EF's generated AlterColumn cannot flip an IDENTITY property in
        // place — SQL Server refuses ("the column needs to be dropped and recreated").
        // AttemptNumber is part of the composite primary key, so the key has to come off
        // before the column can be recreated without its identity, then go back on.
        //
        // Safe to drop and recreate the column outright: PaymentAttempts has never held
        // a row (attempts are only written when a payment goes indeterminate, which
        // nothing exercised until the sweep did), so there is no data to preserve.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PaymentAttempts",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                schema: "payments",
                table: "PaymentAttempts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaymentAttempts",
                schema: "payments",
                table: "PaymentAttempts",
                columns: new[] { "PaymentId", "AttemptNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PaymentAttempts",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                schema: "payments",
                table: "PaymentAttempts",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaymentAttempts",
                schema: "payments",
                table: "PaymentAttempts",
                columns: new[] { "PaymentId", "AttemptNumber" });
        }
    }
}
