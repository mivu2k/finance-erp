using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdmsPushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPendingApproval",
                table: "BiometricDevices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastContactAddress",
                table: "BiometricDevices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastContactAtUtc",
                table: "BiometricDevices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "BiometricDevices",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPendingApproval",
                table: "BiometricDevices");

            migrationBuilder.DropColumn(
                name: "LastContactAddress",
                table: "BiometricDevices");

            migrationBuilder.DropColumn(
                name: "LastContactAtUtc",
                table: "BiometricDevices");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "BiometricDevices");
        }
    }
}
