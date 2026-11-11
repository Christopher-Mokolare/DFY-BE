using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261111143000_AddManualIdentityReviewerClaimsAndAuditAttribution")]
    public partial class AddManualIdentityReviewerClaimsAndAuditAttribution : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewClaimedByUserId",
                table: "IdentityVerificationSubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewClaimExpiresAt",
                table: "IdentityVerificationSubmissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewClaimHeartbeatAt",
                table: "IdentityVerificationSubmissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActorUserId",
                table: "IdentityVerificationEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmissionId",
                table: "IdentityVerificationEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentSide",
                table: "IdentityVerificationEvents",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationSubmissions_ReviewClaimExpiresAt",
                table: "IdentityVerificationSubmissions",
                column: "ReviewClaimExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationEvents_Submission_Actor_ReceivedAt",
                table: "IdentityVerificationEvents",
                columns: new[] { "SubmissionId", "ActorUserId", "ReceivedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityVerificationSubmissions_ReviewClaimExpiresAt",
                table: "IdentityVerificationSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_IdentityVerificationEvents_Submission_Actor_ReceivedAt",
                table: "IdentityVerificationEvents");

            migrationBuilder.DropColumn(name: "ReviewClaimedByUserId", table: "IdentityVerificationSubmissions");
            migrationBuilder.DropColumn(name: "ReviewClaimExpiresAt", table: "IdentityVerificationSubmissions");
            migrationBuilder.DropColumn(name: "ReviewClaimHeartbeatAt", table: "IdentityVerificationSubmissions");
            migrationBuilder.DropColumn(name: "ActorUserId", table: "IdentityVerificationEvents");
            migrationBuilder.DropColumn(name: "SubmissionId", table: "IdentityVerificationEvents");
            migrationBuilder.DropColumn(name: "DocumentSide", table: "IdentityVerificationEvents");
        }
    }
}
