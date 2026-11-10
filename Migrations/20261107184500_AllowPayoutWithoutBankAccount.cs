using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261107184500_AllowPayoutWithoutBankAccount")]
public partial class AllowPayoutWithoutBankAccount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "BankAccountId",
            table: "Payouts",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the column nullable: queued payouts without a bank account are
        // valid marketplace state and must not be deleted during rollback.
    }
}
