using Hr.Domain;
using Hr.Infrastructure.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hr.Web;

public static class ExportEndpoints
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapHrExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/hr/export");

        group.MapGet("/attendance/daily/{date}", async (
            DateOnly date, IAttendanceService attendance, IHrExportService export) =>
        {
            var days = await attendance.GetDayRegisterAsync(date);
            return Results.File(export.DailyRegister(date, days),
                ExcelContentType, $"attendance-{date:yyyy-MM-dd}.xlsx");
        }).RequireAuthorization(HrPermissions.AttendanceViewAll);

        group.MapGet("/attendance/monthly/{year:int}/{month:int}", async (
            int year, int month, IAttendanceService attendance, IHrExportService export) =>
        {
            if (month is < 1 or > 12) return Results.BadRequest("Month must be 1–12.");

            var rows = await attendance.GetMonthlyAsync(year, month);
            return Results.File(export.MonthlyAttendance(year, month, rows),
                ExcelContentType, $"attendance-{year}-{month:00}.xlsx");
        }).RequireAuthorization(HrPermissions.AttendanceViewAll);

        group.MapGet("/leave", async (ILeaveService leave, IHrExportService export) =>
        {
            var requests = await leave.ListAsync(new LeaveFilter());
            return Results.File(export.LeaveRegister(requests),
                ExcelContentType, $"leave-{DateTime.Today:yyyy-MM-dd}.xlsx");
        }).RequireAuthorization(HrPermissions.LeaveViewAll);

        return app;
    }
}
