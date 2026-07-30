using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpPlatform.Shared.Identity.Migrations
{
    /// <inheritdoc />
    public partial class LabelTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LabelTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DocumentType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WidthMm = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    HeightMm = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    MarginMm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: false),
                    FieldKeys = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShowTitle = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ShowCompanyName = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ShowBarcode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ShowQrCode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    FontScale = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: false),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplates", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_DocumentType_Name",
                table: "LabelTemplates",
                columns: new[] { "DocumentType", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LabelTemplates");
        }
    }
}
