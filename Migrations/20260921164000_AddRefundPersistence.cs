using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DoForYou.API.Migrations;

public partial class AddRefundPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Refunds",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TaskId = table.Column<int>(type: "integer", nullable: false),
                RefundId = table.Column<string>(type: "text", nullable: false),
                TransactionId = table.Column<string>(type: "text", nullable: false),
                Amount = table.Column<decimal>(type: "numeric", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: true),
                FailureReason = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastReconciledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Refunds", x => x.Id);
                table.ForeignKey(
                    name: "FK_Refunds_Tasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "Tasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Refunds_TaskId",
            table: "Refunds",
            column: "TaskId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Refunds_RefundId",
            table: "Refunds",
            column: "RefundId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Refunds");
    }
}
