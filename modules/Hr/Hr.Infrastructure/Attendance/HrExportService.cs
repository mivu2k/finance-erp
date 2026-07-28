using ClosedXML.Excel;
using Hr.Domain;

namespace Hr.Infrastructure.Attendance;

public interface IHrExportService
{
    /// <summary>The monthly grid: one row per employee, one column per day, plus totals.</summary>
    byte[] MonthlyAttendance(int year, int month, IReadOnlyList<MonthlySummary> rows);
    /// <summary>One day's register with in/out times.</summary>
    byte[] DailyRegister(DateOnly date, IReadOnlyList<AttendanceDay> days);
    byte[] LeaveRegister(IReadOnlyList<LeaveRequest> requests);
}

public class HrExportService : IHrExportService
{
    public byte[] MonthlyAttendance(int year, int month, IReadOnlyList<MonthlySummary> rows)
    {
        var days = DateTime.DaysInMonth(year, month);

        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add($"{year}-{month:00}");

        sheet.Cell(1, 1).Value = $"Attendance — {new DateOnly(year, month, 1):MMMM yyyy}";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 13;

        var header = 3;
        sheet.Cell(header, 1).Value = "Code";
        sheet.Cell(header, 2).Value = "Employee";
        sheet.Cell(header, 3).Value = "Department";

        for (var d = 1; d <= days; d++)
        {
            var cell = sheet.Cell(header, 3 + d);
            cell.Value = d;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var totals = new[] { "Present", "Late", "Half", "Absent", "Leave", "Incomplete",
                             "Payable", "Worked (h)", "OT (h)" };
        for (var i = 0; i < totals.Length; i++)
            sheet.Cell(header, 4 + days + i).Value = totals[i];

        sheet.Row(header).Style.Font.Bold = true;
        sheet.Row(header).Style.Fill.BackgroundColor = XLColor.LightGray;

        var r = header + 1;
        foreach (var row in rows)
        {
            sheet.Cell(r, 1).Value = row.EmployeeCode;
            sheet.Cell(r, 2).Value = row.EmployeeName;
            sheet.Cell(r, 3).Value = row.Department ?? "";

            for (var d = 1; d <= days; d++)
            {
                var cell = sheet.Cell(r, 3 + d);
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                if (!row.Days.TryGetValue(d, out var day)) continue;

                cell.Value = Mark(day.Status);
                cell.Style.Fill.BackgroundColor = Colour(day.Status);

                if (day.FirstIn is { } inAt)
                    cell.GetComment().AddText(
                        $"{inAt:HH\\:mm}–{day.LastOut:HH\\:mm} ({day.WorkedMinutes / 60.0:0.0}h)");
            }

            var c = 4 + days;
            sheet.Cell(r, c++).Value = row.Present;
            sheet.Cell(r, c++).Value = row.Late;
            sheet.Cell(r, c++).Value = row.HalfDays;
            sheet.Cell(r, c++).Value = row.Absent;
            sheet.Cell(r, c++).Value = row.OnLeave;
            sheet.Cell(r, c++).Value = row.Incomplete;
            sheet.Cell(r, c++).Value = row.PayableDays;
            sheet.Cell(r, c++).Value = Math.Round(row.WorkedMinutes / 60.0, 1);
            sheet.Cell(r, c).Value = Math.Round(row.OvertimeMinutes / 60.0, 1);

            r++;
        }

        // Legend, so a printed sheet explains itself.
        var legend = r + 2;
        sheet.Cell(legend, 1).Value = "Legend";
        sheet.Cell(legend, 1).Style.Font.Bold = true;
        var col = 2;
        foreach (var status in Enum.GetValues<AttendanceStatus>())
        {
            var cell = sheet.Cell(legend, col++);
            cell.Value = $"{Mark(status)} = {status}";
            cell.Style.Fill.BackgroundColor = Colour(status);
        }

        sheet.Columns(1, 3).AdjustToContents();
        sheet.SheetView.FreezeRows(header);
        sheet.SheetView.FreezeColumns(3);

        return Save(book);
    }

    public byte[] DailyRegister(DateOnly date, IReadOnlyList<AttendanceDay> days)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add(date.ToString("yyyy-MM-dd"));

        sheet.Cell(1, 1).Value = $"Attendance register — {date:dddd, dd MMMM yyyy}";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 13;

        var headers = new[] { "Code", "Employee", "Department", "Status", "In", "Out",
                              "Worked (h)", "Late (min)", "Early (min)", "OT (min)",
                              "Source", "Notes" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(3, i + 1).Value = headers[i];
        sheet.Row(3).Style.Font.Bold = true;
        sheet.Row(3).Style.Fill.BackgroundColor = XLColor.LightGray;

        var r = 4;
        foreach (var day in days)
        {
            sheet.Cell(r, 1).Value = day.Employee.EmployeeCode;
            sheet.Cell(r, 2).Value = day.Employee.FullName;
            sheet.Cell(r, 3).Value = day.Employee.Department?.Name ?? "";
            sheet.Cell(r, 4).Value = day.Status.ToString();
            sheet.Cell(r, 4).Style.Fill.BackgroundColor = Colour(day.Status);
            sheet.Cell(r, 5).Value = day.FirstIn?.ToString("HH\\:mm") ?? "";
            sheet.Cell(r, 6).Value = day.LastOut?.ToString("HH\\:mm") ?? "";
            sheet.Cell(r, 7).Value = Math.Round(day.WorkedMinutes / 60.0, 1);
            sheet.Cell(r, 8).Value = day.LateMinutes;
            sheet.Cell(r, 9).Value = day.EarlyLeaveMinutes;
            sheet.Cell(r, 10).Value = day.OvertimeMinutes;
            sheet.Cell(r, 11).Value = day.Source.ToString();
            sheet.Cell(r, 12).Value = day.OverrideReason ?? day.Notes ?? "";
            r++;
        }

        sheet.Columns(1, headers.Length).AdjustToContents();
        sheet.SheetView.FreezeRows(3);
        return Save(book);
    }

    public byte[] LeaveRegister(IReadOnlyList<LeaveRequest> requests)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Leave");

        var headers = new[] { "Request", "Employee", "Type", "From", "To", "Days",
                              "Status", "Reason", "Decided by", "Decided on", "Note" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;

        var r = 2;
        foreach (var l in requests)
        {
            sheet.Cell(r, 1).Value = l.RequestNumber;
            sheet.Cell(r, 2).Value = l.Employee.FullName;
            sheet.Cell(r, 3).Value = l.LeaveType.Name;
            sheet.Cell(r, 4).Value = l.FromDate.ToString("yyyy-MM-dd");
            sheet.Cell(r, 5).Value = l.ToDate.ToString("yyyy-MM-dd");
            sheet.Cell(r, 6).Value = l.Days;
            sheet.Cell(r, 7).Value = l.Status.ToString();
            sheet.Cell(r, 8).Value = l.Reason;
            sheet.Cell(r, 9).Value = l.DecidedByName ?? "";
            sheet.Cell(r, 10).Value = l.DecidedAtUtc?.ToString("yyyy-MM-dd") ?? "";
            sheet.Cell(r, 11).Value = l.DecisionNote ?? "";
            r++;
        }

        sheet.Columns(1, headers.Length).AdjustToContents();
        sheet.SheetView.FreezeRows(1);
        return Save(book);
    }

    /// <summary>Single-letter marks, the way a paper muster roll reads.</summary>
    private static string Mark(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => "P",
        AttendanceStatus.Late => "L",
        AttendanceStatus.HalfDay => "H",
        AttendanceStatus.Absent => "A",
        AttendanceStatus.OnLeave => "LV",
        AttendanceStatus.Holiday => "HO",
        AttendanceStatus.WeeklyOff => "W",
        AttendanceStatus.Incomplete => "?",
        _ => ""
    };

    private static XLColor Colour(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => XLColor.FromHtml("#C8E6C9"),
        AttendanceStatus.Late => XLColor.FromHtml("#FFE0B2"),
        AttendanceStatus.HalfDay => XLColor.FromHtml("#FFF9C4"),
        AttendanceStatus.Absent => XLColor.FromHtml("#FFCDD2"),
        AttendanceStatus.OnLeave => XLColor.FromHtml("#BBDEFB"),
        AttendanceStatus.Holiday => XLColor.FromHtml("#E1BEE7"),
        AttendanceStatus.WeeklyOff => XLColor.FromHtml("#ECEFF1"),
        AttendanceStatus.Incomplete => XLColor.FromHtml("#FFCCBC"),
        _ => XLColor.NoColor
    };

    private static byte[] Save(XLWorkbook book)
    {
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }
}
