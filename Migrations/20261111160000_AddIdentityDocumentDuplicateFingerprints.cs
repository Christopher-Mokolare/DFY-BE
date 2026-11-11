using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261111160000_AddIdentityDocumentDuplicateFingerprints")]
    public partial class AddIdentityDocumentDuplicateFingerprints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityDocumentIdHmac",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentIdHmac",
                table: "IdentityVerificationSubmissions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DuplicateIdDetected",
                table: "IdentityVerificationSubmissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdentityDocumentIdHmac",
                table: "Users",
                column: "IdentityDocumentIdHmac");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationSubmissions_DocumentIdHmac",
                table: "IdentityVerificationSubmissions",
                column: "DocumentIdHmac");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Users_IdentityDocumentIdHmac", table: "Users");
            migrationBuilder.DropIndex(name: "IX_IdentityVerificationSubmissions_DocumentIdHmac", table: "IdentityVerificationSubmissions");
            migrationBuilder.DropColumn(name: "IdentityDocumentIdHmac", table: "Users");
            migrationBuilder.DropColumn(name: "DocumentIdHmac", table: "IdentityVerificationSubmissions");
            migrationBuilder.DropColumn(name: "DuplicateIdDetected", table: "IdentityVerificationSubmissions");
        }
    }
}
