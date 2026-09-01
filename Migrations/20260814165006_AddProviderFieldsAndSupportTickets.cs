using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DoForYou.API.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderFieldsAndSupportTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Users' AND column_name='Bio') THEN
                        ALTER TABLE ""Users"" ADD ""Bio"" text;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Users' AND column_name='CoverageArea') THEN
                        ALTER TABLE ""Users"" ADD ""CoverageArea"" text;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Users' AND column_name='IsAvailable') THEN
                        ALTER TABLE ""Users"" ADD ""IsAvailable"" boolean NOT NULL DEFAULT true;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Users' AND column_name='ProfilePhotoUrl') THEN
                        ALTER TABLE ""Users"" ADD ""ProfilePhotoUrl"" text;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Users' AND column_name='ServiceCategories') THEN
                        ALTER TABLE ""Users"" ADD ""ServiceCategories"" text;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='SupportTickets') THEN
                        CREATE TABLE ""SupportTickets"" (
                            ""Id"" serial PRIMARY KEY,
                            ""UserId"" integer REFERENCES ""Users""(""Id""),
                            ""Name"" text NOT NULL DEFAULT '',
                            ""Email"" text NOT NULL DEFAULT '',
                            ""Subject"" text NOT NULL DEFAULT '',
                            ""Message"" text NOT NULL DEFAULT '',
                            ""Category"" text NOT NULL DEFAULT 'General',
                            ""Priority"" text NOT NULL DEFAULT 'Normal',
                            ""Status"" text NOT NULL DEFAULT 'Open',
                            ""AssignedTo"" text,
                            ""AdminNotes"" text,
                            ""Reference"" text NOT NULL DEFAULT '',
                            ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                            ""ResolvedAt"" timestamp with time zone
                        );
                        CREATE INDEX ""IX_SupportTickets_UserId"" ON ""SupportTickets""(""UserId"");
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "Bio",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CoverageArea",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsAvailable",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProfilePhotoUrl",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ServiceCategories",
                table: "Users");
        }
    }
}
