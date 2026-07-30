using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IpAddress = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Browser = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Action = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntityName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntityId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OldValues = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NewValues = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TimestampUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Ledgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CounterpartyName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CounterpartyPhone = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CounterpartyAddress = table.Column<string>(type: "varchar(400)", maxLength: 400, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Nature = table.Column<int>(type: "int", nullable: false),
                    ParentLedgerId = table.Column<int>(type: "int", nullable: true),
                    OpeningBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OpenedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FinanceAccountId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ledgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ledgers_Ledgers_ParentLedgerId",
                        column: x => x.ParentLedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Value = table.Column<string>(type: "varchar(400)", maxLength: 400, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PlainLedgerId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Method = table.Column<int>(type: "int", nullable: false),
                    CounterLedgerId = table.Column<int>(type: "int", nullable: true),
                    TransferGroup = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    RecordedById = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecordedByName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PostedVoucherId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entries_Ledgers_CounterLedgerId",
                        column: x => x.CounterLedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entries_Ledgers_PlainLedgerId",
                        column: x => x.PlainLedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityName_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityName", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_CounterLedgerId",
                table: "Entries",
                column: "CounterLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Date",
                table: "Entries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_PlainLedgerId",
                table: "Entries",
                column: "PlainLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_TransferGroup",
                table: "Entries",
                column: "TransferGroup");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_Name",
                table: "Ledgers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_ParentLedgerId",
                table: "Ledgers",
                column: "ParentLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_Status",
                table: "Ledgers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_Key",
                table: "Settings",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Ledgers");
        }
    }
}
