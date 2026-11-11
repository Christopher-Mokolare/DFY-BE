using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261111093000_AddManualIdentityVerificationSubmissions")]
    public partial class AddManualIdentityVerificationSubmissions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityVerificationSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FrontCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    FrontNonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    FrontTag = table.Column<byte[]>(type: "bytea", nullable: false),
                    FrontContentType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BackCiphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    BackNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    BackTag = table.Column<byte[]>(type: "bytea", nullable: true),
                    BackContentType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ConsentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    InternalReviewNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DocumentsDeleteAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DocumentsPurgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityVerificationSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityVerificationSubmissions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityVerificationSubmissions_Users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationSubmissions_Status_SubmittedAt",
                table: "IdentityVerificationSubmissions",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationSubmissions_UserId_SubmittedAt",
                table: "IdentityVerificationSubmissions",
                columns: new[] { "UserId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationSubmissions_ReviewedByUserId",
                table: "IdentityVerificationSubmissions",
                column: "ReviewedByUserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IdentityVerificationSubmissions");
        }
    }
}
