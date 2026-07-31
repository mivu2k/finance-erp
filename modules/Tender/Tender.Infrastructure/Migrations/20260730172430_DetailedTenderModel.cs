using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tender.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DetailedTenderModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BidValidityDays",
                table: "Tenders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionPeriodDays",
                table: "Tenders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefectLiabilityPeriodMonths",
                table: "Tenders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmdExemptionReason",
                table: "Tenders",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsEmdExempted",
                table: "Tenders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "L1Amount",
                table: "Tenders",
                type: "decimal(16,2)",
                precision: 16,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OurRank",
                table: "Tenders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentTerms",
                table: "Tenders",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PortalReference",
                table: "Tenders",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "RetentionMoneyPercentage",
                table: "Tenders",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmissionMode",
                table: "Tenders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TenderFee",
                table: "Tenders",
                type: "decimal(16,2)",
                precision: 16,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkOrderNumber",
                table: "Tenders",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BankContactPerson",
                table: "Guarantees",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BankContactPhone",
                table: "Guarantees",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "Charges",
                table: "Guarantees",
                type: "decimal(16,2)",
                precision: 16,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenewalOfGuaranteeId",
                table: "Guarantees",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Competitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TenderRecordId = table.Column<int>(type: "int", nullable: false),
                    BidderName = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    QuotedAmount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: true),
                    IsOwnBid = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_Competitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Competitors_Tenders_TenderRecordId",
                        column: x => x.TenderRecordId,
                        principalTable: "Tenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Guarantees_RenewalOfGuaranteeId",
                table: "Guarantees",
                column: "RenewalOfGuaranteeId");

            migrationBuilder.CreateIndex(
                name: "IX_Competitors_TenderRecordId",
                table: "Competitors",
                column: "TenderRecordId");

            migrationBuilder.AddForeignKey(
                name: "FK_Guarantees_Guarantees_RenewalOfGuaranteeId",
                table: "Guarantees",
                column: "RenewalOfGuaranteeId",
                principalTable: "Guarantees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Guarantees_Guarantees_RenewalOfGuaranteeId",
                table: "Guarantees");

            migrationBuilder.DropTable(
                name: "Competitors");

            migrationBuilder.DropIndex(
                name: "IX_Guarantees_RenewalOfGuaranteeId",
                table: "Guarantees");

            migrationBuilder.DropColumn(
                name: "BidValidityDays",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "CompletionPeriodDays",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "DefectLiabilityPeriodMonths",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "EmdExemptionReason",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "IsEmdExempted",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "L1Amount",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "OurRank",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "PaymentTerms",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "PortalReference",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "RetentionMoneyPercentage",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "SubmissionMode",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "TenderFee",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "WorkOrderNumber",
                table: "Tenders");

            migrationBuilder.DropColumn(
                name: "BankContactPerson",
                table: "Guarantees");

            migrationBuilder.DropColumn(
                name: "BankContactPhone",
                table: "Guarantees");

            migrationBuilder.DropColumn(
                name: "Charges",
                table: "Guarantees");

            migrationBuilder.DropColumn(
                name: "RenewalOfGuaranteeId",
                table: "Guarantees");
        }
    }
}
