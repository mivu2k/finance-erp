using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentRequestProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                table: "PaymentRequests",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_ProjectId",
                table: "PaymentRequests",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Projects_ProjectId",
                table: "PaymentRequests",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Projects_ProjectId",
                table: "PaymentRequests");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_ProjectId",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PaymentRequests");
        }
    }
}
