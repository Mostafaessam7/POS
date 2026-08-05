using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Sync.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sync");

            migrationBuilder.CreateTable(
                name: "Batches",
                schema: "sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    ProtocolVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MasterDataVersions",
                schema: "sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterDataVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncedRecords",
                schema: "sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalSequence = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncedRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalSyncCursors",
                schema: "sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcknowledgedVersion = table.Column<long>(type: "bigint", nullable: false),
                    LastAcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalSyncCursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_Status_ReceivedAt",
                schema: "sync",
                table: "Batches",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_TenantId_TerminalId_ReceivedAt",
                schema: "sync",
                table: "Batches",
                columns: new[] { "TenantId", "TerminalId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataVersions_TenantId_EntityType",
                schema: "sync",
                table: "MasterDataVersions",
                columns: new[] { "TenantId", "EntityType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncedRecords_BatchId",
                schema: "sync",
                table: "SyncedRecords",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncedRecords_RecordId",
                schema: "sync",
                table: "SyncedRecords",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "UX_SyncedRecords_Terminal_Sequence",
                schema: "sync",
                table: "SyncedRecords",
                columns: new[] { "TerminalId", "TerminalSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TerminalSyncCursors_TenantId_TerminalId_EntityType",
                schema: "sync",
                table: "TerminalSyncCursors",
                columns: new[] { "TenantId", "TerminalId", "EntityType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Batches",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "MasterDataVersions",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "SyncedRecords",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "TerminalSyncCursors",
                schema: "sync");
        }
    }
}
