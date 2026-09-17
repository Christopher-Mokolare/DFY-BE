using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    /// <inheritdoc />
    public partial class HardenPayoutIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Payouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "Payouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "Payouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingError",
                table: "Payouts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_ProviderReference",
                table: "Payouts",
                column: "ProviderReference",
                unique: true,
                filter: "\"ProviderReference\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payouts_ProviderReference",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "ProcessingError",
                table: "Payouts");
        }
    }
}
