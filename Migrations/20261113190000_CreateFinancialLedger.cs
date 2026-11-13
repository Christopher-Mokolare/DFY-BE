using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DoForYou.API.Migrations
{
    public partial class CreateFinancialLedger : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancialAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ZAR"),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_FinancialAccounts", x => x.Id));

            migrationBuilder.CreateTable(
                name: "FinancialJournals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ZAR"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ReversalOfJournalId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialJournals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialJournals_FinancialJournals_ReversalOfJournalId",
                        column: x => x.ReversalOfJournalId,
                        principalTable: "FinancialJournals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialJournalLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ZAR"),
                    Memo = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialJournalLines", x => x.Id);
                    table.CheckConstraint(
                        "CK_FinancialJournalLines_OneSidedPositiveAmount",
                        "((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Credit\" > 0 AND \"Debit\" = 0))");
                    table.CheckConstraint(
                        "CK_FinancialJournalLines_NonNegativeAmounts",
                        "\"Debit\" >= 0 AND \"Credit\" >= 0");
                    table.ForeignKey(
                        name: "FK_FinancialJournalLines_FinancialAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "FinancialAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialJournalLines_FinancialJournals_JournalId",
                        column: x => x.JournalId,
                        principalTable: "FinancialJournals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_FinancialAccounts_Code", table: "FinancialAccounts", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_FinancialJournals_JournalNumber", table: "FinancialJournals", column: "JournalNumber", unique: true);
            migrationBuilder.CreateIndex(name: "IX_FinancialJournals_IdempotencyKey", table: "FinancialJournals", column: "IdempotencyKey", unique: true);
            migrationBuilder.CreateIndex(name: "IX_FinancialJournals_SourceType_SourceId_EventType", table: "FinancialJournals", columns: new[] { "SourceType", "SourceId", "EventType" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_FinancialJournals_ReversalOfJournalId", table: "FinancialJournals", column: "ReversalOfJournalId", unique: true, filter: "\"ReversalOfJournalId\" IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_FinancialJournalLines_JournalId_LineNumber", table: "FinancialJournalLines", columns: new[] { "JournalId", "LineNumber" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_FinancialJournalLines_AccountId", table: "FinancialJournalLines", column: "AccountId");

            migrationBuilder.InsertData(
                table: "FinancialAccounts",
                columns: new[] { "Id", "Code", "Name", "AccountType", "Currency", "IsSystem", "IsActive", "CreatedAt" },
                values: new object[,]
                {
                    { 1, "1000", "Payment Provider Clearing", "Asset", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, "1100", "Merchant Bank Settlement", "Asset", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, "2000", "Escrow Liability", "Liability", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, "2100", "Runner Payable", "Liability", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, "2200", "Unallocated Paystack Receipts", "Liability", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, "4000", "Platform Commission Revenue", "Revenue", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, "5000", "Payment Processing Fees", "Expense", "ZAR", true, true, new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION prevent_financial_ledger_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Financial journals and journal lines are append-only';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_FinancialJournals_AppendOnly"
                BEFORE UPDATE OR DELETE ON "FinancialJournals"
                FOR EACH ROW EXECUTE FUNCTION prevent_financial_ledger_mutation();

                CREATE TRIGGER "TR_FinancialJournalLines_AppendOnly"
                BEFORE UPDATE OR DELETE ON "FinancialJournalLines"
                FOR EACH ROW EXECUTE FUNCTION prevent_financial_ledger_mutation();

                CREATE TRIGGER "TR_FinancialJournals_NoTruncate"
                BEFORE TRUNCATE ON "FinancialJournals"
                FOR EACH STATEMENT EXECUTE FUNCTION prevent_financial_ledger_mutation();

                CREATE TRIGGER "TR_FinancialJournalLines_NoTruncate"
                BEFORE TRUNCATE ON "FinancialJournalLines"
                FOR EACH STATEMENT EXECUTE FUNCTION prevent_financial_ledger_mutation();

                CREATE OR REPLACE FUNCTION enforce_financial_journal_balance()
                RETURNS trigger AS $$
                DECLARE
                    target_journal_id bigint;
                    line_count integer;
                    debit_total numeric(18,2);
                    credit_total numeric(18,2);
                BEGIN
                    IF TG_TABLE_NAME = 'FinancialJournals' THEN
                        target_journal_id := NEW."Id";
                    ELSE
                        target_journal_id := NEW."JournalId";
                    END IF;

                    SELECT COUNT(*), COALESCE(SUM("Debit"), 0), COALESCE(SUM("Credit"), 0)
                    INTO line_count, debit_total, credit_total
                    FROM "FinancialJournalLines"
                    WHERE "JournalId" = target_journal_id;

                    IF line_count < 2 OR debit_total <= 0 OR debit_total <> credit_total THEN
                        RAISE EXCEPTION 'Financial journal % is not balanced (lines %, debit %, credit %)',
                            target_journal_id, line_count, debit_total, credit_total;
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER "TR_FinancialJournals_Balanced"
                AFTER INSERT ON "FinancialJournals"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION enforce_financial_journal_balance();

                CREATE CONSTRAINT TRIGGER "TR_FinancialJournalLines_Balanced"
                AFTER INSERT ON "FinancialJournalLines"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION enforce_financial_journal_balance();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_FinancialJournalLines_Balanced" ON "FinancialJournalLines";
                DROP TRIGGER IF EXISTS "TR_FinancialJournals_Balanced" ON "FinancialJournals";
                DROP TRIGGER IF EXISTS "TR_FinancialJournalLines_NoTruncate" ON "FinancialJournalLines";
                DROP TRIGGER IF EXISTS "TR_FinancialJournals_NoTruncate" ON "FinancialJournals";
                DROP TRIGGER IF EXISTS "TR_FinancialJournalLines_AppendOnly" ON "FinancialJournalLines";
                DROP TRIGGER IF EXISTS "TR_FinancialJournals_AppendOnly" ON "FinancialJournals";
                DROP FUNCTION IF EXISTS enforce_financial_journal_balance();
                DROP FUNCTION IF EXISTS prevent_financial_ledger_mutation();
                """);
            migrationBuilder.DropTable(name: "FinancialJournalLines");
            migrationBuilder.DropTable(name: "FinancialJournals");
            migrationBuilder.DropTable(name: "FinancialAccounts");
        }
    }
}
