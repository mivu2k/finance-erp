using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequestLineReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentName",
                table: "PaymentRequestLines",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AttachmentPath",
                table: "PaymentRequestLines",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentName",
                table: "PaymentRequestLines");

            migrationBuilder.DropColumn(
                name: "AttachmentPath",
                table: "PaymentRequestLines");
        }
    }
}
