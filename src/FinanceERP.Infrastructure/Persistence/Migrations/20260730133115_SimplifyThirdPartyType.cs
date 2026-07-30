using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceERP.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Collapses <c>ThirdPartyType</c> from seven values to Receivable/Payable.
    /// </summary>
    /// <remarks>
    /// The enum is stored as an int, so there is no schema change here — only a data
    /// remap, which EF cannot scaffold. Without it, existing rows would keep values
    /// (3..7, 99) that no longer name anything and the party editor would render a
    /// blank type.
    /// <para>
    /// The mapping reproduces exactly how the old code placed a party's account:
    /// <c>Customer or Borrower</c> hung under Receivables (1600) and everything else
    /// under Payables (2100). So Customer (1) and Borrower (7) become Receivable (1),
    /// and Supplier/Vendor/Contractor/Investor/Lender/Other become Payable (2).
    /// Accounts already created are left exactly where they are.
    /// </para>
    /// </remarks>
    public partial class SimplifyThirdPartyType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE `ThirdParties` SET `Type` = CASE WHEN `Type` IN (1, 7) THEN 1 ELSE 2 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy on purpose: the old seven-way distinction isn't recoverable once
            // collapsed, so this only restores values the old enum understood —
            // Receivable back to Customer, Payable back to Supplier.
            migrationBuilder.Sql(
                "UPDATE `ThirdParties` SET `Type` = CASE WHEN `Type` = 1 THEN 1 ELSE 2 END;");
        }
    }
}
