using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KioskAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Punches collected by the terminals are discarded with them. They carry
            // a device enrolment id and no employee on the unmatched ones, so they
            // cannot satisfy the non-null EmployeeId or the unique (EmployeeId,
            // PunchedAt) index below — and the derived days are rebuilt from
            // punches, so keeping half of them would be worse than keeping none.
            migrationBuilder.Sql("DELETE FROM AttendanceDays;");
            migrationBuilder.Sql("DELETE FROM AttendancePunches;");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePunches_BiometricDevices_BiometricDeviceId",
                table: "AttendancePunches");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePunches_Employees_EmployeeId",
                table: "AttendancePunches");

            migrationBuilder.DropTable(
                name: "BiometricDevices");

            migrationBuilder.DropIndex(
                name: "IX_Employees_DeviceUserId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePunches_EmployeeId_PunchedAt",
                table: "AttendancePunches");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePunches_Natural",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "DeviceUserId",
                table: "AttendancePunches");

            migrationBuilder.RenameColumn(
                name: "DeviceUserId",
                table: "Employees",
                newName: "QrSecret");

            migrationBuilder.RenameColumn(
                name: "VerifyMode",
                table: "AttendancePunches",
                newName: "Method");

            migrationBuilder.RenameColumn(
                name: "BiometricDeviceId",
                table: "AttendancePunches",
                newName: "AttendanceStationId");

            migrationBuilder.RenameIndex(
                name: "IX_AttendancePunches_BiometricDeviceId",
                table: "AttendancePunches",
                newName: "IX_AttendancePunches_AttendanceStationId");

            migrationBuilder.AddColumn<string>(
                name: "CardNumber",
                table: "Employees",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<int>(
                name: "EmployeeId",
                table: "AttendancePunches",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "AttendancePunches",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AttendanceStations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Location = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AccessToken = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastPunchAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastPunchDescription = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
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
                    table.PrimaryKey("PK_AttendanceStations", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CardNumber",
                table: "Employees",
                column: "CardNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunches_Natural",
                table: "AttendancePunches",
                columns: new[] { "EmployeeId", "PunchedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceStations_AccessToken",
                table: "AttendanceStations",
                column: "AccessToken",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePunches_AttendanceStations_AttendanceStationId",
                table: "AttendancePunches",
                column: "AttendanceStationId",
                principalTable: "AttendanceStations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePunches_Employees_EmployeeId",
                table: "AttendancePunches",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePunches_AttendanceStations_AttendanceStationId",
                table: "AttendancePunches");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendancePunches_Employees_EmployeeId",
                table: "AttendancePunches");

            migrationBuilder.DropTable(
                name: "AttendanceStations");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CardNumber",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePunches_Natural",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "CardNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "AttendancePunches");

            migrationBuilder.RenameColumn(
                name: "QrSecret",
                table: "Employees",
                newName: "DeviceUserId");

            migrationBuilder.RenameColumn(
                name: "Method",
                table: "AttendancePunches",
                newName: "VerifyMode");

            migrationBuilder.RenameColumn(
                name: "AttendanceStationId",
                table: "AttendancePunches",
                newName: "BiometricDeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_AttendancePunches_AttendanceStationId",
                table: "AttendancePunches",
                newName: "IX_AttendancePunches_BiometricDeviceId");

            migrationBuilder.AlterColumn<int>(
                name: "EmployeeId",
                table: "AttendancePunches",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "DeviceUserId",
                table: "AttendancePunches",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BiometricDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ClearLogAfterSync = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CommKey = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Host = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsPendingApproval = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastContactAddress = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastContactAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastPunchAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSyncAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSyncPunchCount = table.Column<int>(type: "int", nullable: false),
                    LastSyncResult = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Location = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Port = table.Column<int>(type: "int", nullable: false),
                    SerialNumber = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricDevices", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DeviceUserId",
                table: "Employees",
                column: "DeviceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunches_EmployeeId_PunchedAt",
                table: "AttendancePunches",
                columns: new[] { "EmployeeId", "PunchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunches_Natural",
                table: "AttendancePunches",
                columns: new[] { "DeviceUserId", "PunchedAt", "BiometricDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BiometricDevices_Host_Port",
                table: "BiometricDevices",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePunches_BiometricDevices_BiometricDeviceId",
                table: "AttendancePunches",
                column: "BiometricDeviceId",
                principalTable: "BiometricDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendancePunches_Employees_EmployeeId",
                table: "AttendancePunches",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
