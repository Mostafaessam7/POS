using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransferAndStocktakeCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                schema: "inventory",
                table: "StockTransfers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                schema: "inventory",
                table: "StockTransfers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                schema: "inventory",
                table: "StockTransfers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                schema: "inventory",
                table: "Stocktakes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                schema: "inventory",
                table: "Stocktakes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                schema: "inventory",
                table: "Stocktakes",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationReason",
                schema: "inventory",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "inventory",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                schema: "inventory",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                schema: "inventory",
                table: "Stocktakes");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "inventory",
                table: "Stocktakes");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                schema: "inventory",
                table: "Stocktakes");
        }
    }
}
