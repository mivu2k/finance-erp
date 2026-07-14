using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvanceJustificationFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Vouchers_VoucherId",
                table: "PaymentRequests");

            migrationBuilder.AddColumn<int>(
                name: "AdvanceAccountId",
                table: "PaymentRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "PaymentRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SettlementVoucherId",
                table: "PaymentRequests",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_SettlementVoucherId",
                table: "PaymentRequests",
                column: "SettlementVoucherId");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Vouchers_SettlementVoucherId",
                table: "PaymentRequests",
                column: "SettlementVoucherId",
                principalTable: "Vouchers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Vouchers_VoucherId",
                table: "PaymentRequests",
                column: "VoucherId",
                principalTable: "Vouchers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Vouchers_SettlementVoucherId",
                table: "PaymentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Vouchers_VoucherId",
                table: "PaymentRequests");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_SettlementVoucherId",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "AdvanceAccountId",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "SettlementVoucherId",
                table: "PaymentRequests");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Vouchers_VoucherId",
                table: "PaymentRequests",
                column: "VoucherId",
                principalTable: "Vouchers",
                principalColumn: "Id");
        }
    }
}
