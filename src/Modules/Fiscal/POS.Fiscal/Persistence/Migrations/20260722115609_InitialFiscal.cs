using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fiscal");

            migrationBuilder.CreateTable(
                name: "FiscalDocuments",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    Series = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    FormattedNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CanonicalHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PreviousDocumentHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SignatureAlgorithm = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SignatureValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuthorityIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    QrPayload = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedOffline = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TransmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TransmissionDueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FiscalSequences",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Series = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastAllocated = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FiscalTransmissionAttempts",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AuthorityIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MessageCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    MessageText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsRetryable = table.Column<bool>(type: "bit", nullable: false),
                    FiscalDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalTransmissionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalTransmissionAttempts_FiscalDocuments_FiscalDocumentId",
                        column: x => x.FiscalDocumentId,
                        principalSchema: "fiscal",
                        principalTable: "FiscalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_Chain",
                schema: "fiscal",
                table: "FiscalDocuments",
                columns: new[] { "CompanyId", "TerminalId", "Series", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_Sale",
                schema: "fiscal",
                table: "FiscalDocuments",
                columns: new[] { "TenantId", "SaleId" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_Transmission",
                schema: "fiscal",
                table: "FiscalDocuments",
                columns: new[] { "Status", "TransmissionDueBy" });

            migrationBuilder.CreateIndex(
                name: "UX_FiscalDocuments_Number",
                schema: "fiscal",
                table: "FiscalDocuments",
                columns: new[] { "CompanyId", "TerminalId", "Series", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FiscalSequences_Series",
                schema: "fiscal",
                table: "FiscalSequences",
                columns: new[] { "CompanyId", "TerminalId", "Series" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalTransmissionAttempts_FiscalDocumentId",
                schema: "fiscal",
                table: "FiscalTransmissionAttempts",
                column: "FiscalDocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalSequences",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "FiscalTransmissionAttempts",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "FiscalDocuments",
                schema: "fiscal");
        }
    }
}
