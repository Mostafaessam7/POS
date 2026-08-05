using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProvisioningOperators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionedByOperatorId",
                schema: "identity",
                table: "Tenants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProvisioningOperators",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningOperators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningOperators_KeyHash",
                schema: "identity",
                table: "ProvisioningOperators",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningOperators_Name",
                schema: "identity",
                table: "ProvisioningOperators",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisioningOperators",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "ProvisionedByOperatorId",
                schema: "identity",
                table: "Tenants");
        }
    }
}
