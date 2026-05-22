using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    /// <inheritdoc />
    public partial class SyncSchemaWithModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only add CommissionPercentage — all other columns already exist in the DB
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                  WHERE table_name='Tasks' AND column_name='CommissionPercentage') THEN
                        ALTER TABLE ""Tasks"" ADD ""CommissionPercentage"" numeric(5,2) NOT NULL DEFAULT 15.00;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TaskName",
                table: "Tasks");
        }
    }
}
