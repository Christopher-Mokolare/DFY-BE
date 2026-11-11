using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261109100000_AddIncidentEscrowHolds")]
    public partial class AddIncidentEscrowHolds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EscrowHoldDeadlineBefore",
                table: "TaskIncidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscrowHoldPlacedAt",
                table: "TaskIncidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EscrowHoldPlacedByAdminId",
                table: "TaskIncidents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscrowHoldReason",
                table: "TaskIncidents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscrowHoldReleasedAt",
                table: "TaskIncidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscrowHoldReleaseReason",
                table: "TaskIncidents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "EscrowHoldDeadlineBefore", table: "TaskIncidents");
            migrationBuilder.DropColumn(name: "EscrowHoldPlacedAt", table: "TaskIncidents");
            migrationBuilder.DropColumn(name: "EscrowHoldPlacedByAdminId", table: "TaskIncidents");
            migrationBuilder.DropColumn(name: "EscrowHoldReason", table: "TaskIncidents");
            migrationBuilder.DropColumn(name: "EscrowHoldReleasedAt", table: "TaskIncidents");
            migrationBuilder.DropColumn(name: "EscrowHoldReleaseReason", table: "TaskIncidents");
        }
    }
}