using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261107190000_CorrectPaystackProviderDefault")]
public partial class CorrectPaystackProviderDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
ALTER TABLE ""Payments""
    ALTER COLUMN ""Provider"" SET DEFAULT 'Paystack';
");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the Paystack default on rollback. Restoring the retired Ozow
        // provider as the default would make newly-created payment rows unsafe.
    }
}
