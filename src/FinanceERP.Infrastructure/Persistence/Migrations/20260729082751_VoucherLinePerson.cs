using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoucherLinePerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonId",
                table: "VoucherLines",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PersonName",
                table: "VoucherLines",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherLines_PersonId",
                table: "VoucherLines",
                column: "PersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VoucherLines_PersonId",
                table: "VoucherLines");

            migrationBuilder.DropColumn(
                name: "PersonId",
                table: "VoucherLines");

            migrationBuilder.DropColumn(
                name: "PersonName",
                table: "VoucherLines");
        }
    }
}
