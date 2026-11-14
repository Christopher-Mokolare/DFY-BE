using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261113100000_ProtectAuditLogsAppendOnly")]
    public partial class ProtectAuditLogsAppendOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION prevent_audit_log_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLogs is append-only; UPDATE, DELETE and TRUNCATE are prohibited.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER "TR_AuditLogs_AppendOnly_UpdateDelete"
                BEFORE UPDATE OR DELETE ON "AuditLogs"
                FOR EACH ROW EXECUTE FUNCTION prevent_audit_log_mutation();

                CREATE TRIGGER "TR_AuditLogs_AppendOnly_Truncate"
                BEFORE TRUNCATE ON "AuditLogs"
                FOR EACH STATEMENT EXECUTE FUNCTION prevent_audit_log_mutation();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AuditLogs_AppendOnly_UpdateDelete" ON "AuditLogs";
                DROP TRIGGER IF EXISTS "TR_AuditLogs_AppendOnly_Truncate" ON "AuditLogs";
                DROP FUNCTION IF EXISTS prevent_audit_log_mutation();
                """);
        }
    }
}
