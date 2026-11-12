using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261112100000_AddIdentityNotificationOutbox")]
    public partial class AddIdentityNotificationOutbox : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OutboxId",
                table: "Notifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityNotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecipientUserId = table.Column<int>(type: "integer", nullable: true),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table => { table.PrimaryKey("PK_IdentityNotificationOutbox", x => x.Id); });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityNotificationOutbox_IdempotencyKey",
                table: "IdentityNotificationOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityNotificationOutbox_LeaseExpiresAt",
                table: "IdentityNotificationOutbox",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityNotificationOutbox_Status_AvailableAt",
                table: "IdentityNotificationOutbox",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_OutboxId",
                table: "Notifications",
                column: "OutboxId",
                unique: true,
                filter: "\"OutboxId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IdentityNotificationOutbox");
            migrationBuilder.DropIndex(name: "IX_Notifications_OutboxId", table: "Notifications");
            migrationBuilder.DropColumn(name: "OutboxId", table: "Notifications");
        }
    }
}
