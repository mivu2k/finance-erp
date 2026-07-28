using ErpPlatform.Shared.Kernel;
using Hr.Domain;
using Hr.Infrastructure;
using Hr.Infrastructure.Attendance;
using Hr.Infrastructure.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// Exercises the rebuild against a real MySQL/MariaDB schema — the part the pure
/// calculator tests can't reach, since it depends on EF mapping, query filters and
/// the shift/holiday/leave joins.
/// </summary>
/// <remarks>
/// Uses a throwaway database so it never touches the developer's data. Skipped
/// automatically when no server is reachable, so <c>dotnet test</c> still passes
/// on a machine without one.
/// </remarks>
public class AttendanceRebuildIntegrationTests : IAsyncLifetime
{
    private const string Server = "Server=localhost;Port=3306;User=finance;Password=DevPassword1!;";
    private readonly string _database = $"erp_hr_test_{Guid.NewGuid():N}"[..24];

    private HrDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<HrDbContext>()
            .UseMySql($"{Server}Database={_database};", new MySqlServerVersion(new Version(10, 11, 0)))
            .Options;

        _db = new HrDbContext(options, new TestUser());

        try
        {
            await _db.Database.EnsureCreatedAsync();
            _available = true;
        }
        catch
        {
            // No database on this machine — the pure tests still cover the arithmetic.
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_available) await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    [SkippableFact]
    public async Task Rebuild_derives_a_full_week_from_raw_punches()
    {
        Skip.IfNot(_available, "No database available");

        var shift = new Shift
        {
            Name = "General",
            StartsAt = new TimeOnly(9, 0),
            EndsAt = new TimeOnly(17, 0),
            GraceMinutes = 15,
            HalfDayMinutes = 240,
            MinimumMinutes = 60,
            OvertimeAfterMinutes = 30,
            WeeklyOffMask = 1 << (int)DayOfWeek.Sunday,
            IsDefault = true
        };
        _db.Shifts.Add(shift);

        var kamran = new Employee
        {
            EmployeeCode = "EMP-1", FullName = "Kamran Ali", DeviceUserId = "1042",
            JoinedOn = new DateOnly(2026, 1, 5), Shift = shift
        };
        var sana = new Employee
        {
            EmployeeCode = "EMP-2", FullName = "Sana Iqbal", DeviceUserId = "1043",
            JoinedOn = new DateOnly(2026, 2, 10), Shift = shift
        };
        _db.Employees.AddRange(kamran, sana);

        // Tuesday 28 July is a public holiday in this scenario.
        _db.Holidays.Add(new Holiday { Date = new DateOnly(2026, 7, 28), Name = "Public Holiday" });
        await _db.SaveChangesAsync();

        void Punch(Employee who, string at) => _db.AttendancePunches.Add(new AttendancePunch
        {
            DeviceUserId = who.DeviceUserId!,
            EmployeeId = who.Id,
            PunchedAt = DateTime.Parse(at)
        });

        // Monday: Kamran in early, lunch, back, out late → present with overtime.
        Punch(kamran, "2026-07-27 08:52:11");
        Punch(kamran, "2026-07-27 13:04:02");
        Punch(kamran, "2026-07-27 13:48:30");
        Punch(kamran, "2026-07-27 18:12:44");

        // Monday: Sana arrives past grace and leaves early.
        Punch(sana, "2026-07-27 09:31:05");
        Punch(sana, "2026-07-27 16:20:00");

        // Wednesday: Kamran punches in and never out.
        Punch(kamran, "2026-07-29 08:58:00");
        await _db.SaveChangesAsync();

        var sync = new AttendanceSyncService(
            _db, new UnusedClient(), NullLogger<AttendanceSyncService>.Instance);

        await sync.RebuildAsync(new DateOnly(2026, 7, 26), new DateOnly(2026, 7, 29));

        var days = await _db.AttendanceDays.AsNoTracking().ToListAsync();

        AttendanceDay Day(Employee who, int d) =>
            days.Single(x => x.EmployeeId == who.Id && x.Date == new DateOnly(2026, 7, d));

        // Sunday 26th — weekly off for both, no punches.
        Assert.Equal(AttendanceStatus.WeeklyOff, Day(kamran, 26).Status);

        // Monday 27th — Kamran present, bracketed by first and last punch.
        var kamranMonday = Day(kamran, 27);
        Assert.Equal(AttendanceStatus.Present, kamranMonday.Status);
        Assert.Equal(new TimeOnly(8, 52, 11), kamranMonday.FirstIn);
        Assert.Equal(new TimeOnly(18, 12, 44), kamranMonday.LastOut);
        Assert.Equal(4, kamranMonday.PunchCount);
        Assert.Equal(0, kamranMonday.LateMinutes);
        Assert.Equal(72, kamranMonday.OvertimeMinutes);

        // Monday 27th — Sana late and away early.
        var sanaMonday = Day(sana, 27);
        Assert.Equal(AttendanceStatus.Late, sanaMonday.Status);
        Assert.Equal(31, sanaMonday.LateMinutes);
        Assert.Equal(40, sanaMonday.EarlyLeaveMinutes);

        // Tuesday 28th — holiday for everyone.
        Assert.Equal(AttendanceStatus.Holiday, Day(kamran, 28).Status);
        Assert.Equal(AttendanceStatus.Holiday, Day(sana, 28).Status);

        // Wednesday 29th — Kamran's missing punch-out, Sana simply absent.
        Assert.Equal(AttendanceStatus.Incomplete, Day(kamran, 29).Status);
        Assert.Equal(AttendanceStatus.Absent, Day(sana, 29).Status);
    }

    [SkippableFact]
    public async Task Rebuild_leaves_a_hand_corrected_day_alone()
    {
        Skip.IfNot(_available, "No database available");

        var shift = new Shift { Name = "General", IsDefault = true };
        _db.Shifts.Add(shift);

        var employee = new Employee
        {
            EmployeeCode = "EMP-9", FullName = "Test Person",
            JoinedOn = new DateOnly(2026, 1, 1), Shift = shift
        };
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        // A day HR fixed by hand after the terminal missed the reads entirely.
        _db.AttendanceDays.Add(new AttendanceDay
        {
            EmployeeId = employee.Id,
            Date = new DateOnly(2026, 7, 27),
            FirstIn = new TimeOnly(9, 0),
            LastOut = new TimeOnly(17, 0),
            Status = AttendanceStatus.Present,
            Source = AttendanceSource.Manual,
            OverrideReason = "Device offline; verified against the gate register",
            OverriddenByName = "HR Officer"
        });
        await _db.SaveChangesAsync();

        var sync = new AttendanceSyncService(
            _db, new UnusedClient(), NullLogger<AttendanceSyncService>.Instance);

        await sync.RebuildAsync(new DateOnly(2026, 7, 27), new DateOnly(2026, 7, 27));

        var day = await _db.AttendanceDays.AsNoTracking()
            .SingleAsync(d => d.EmployeeId == employee.Id);

        // Without the manual guard this would have flipped to Absent, silently
        // undoing the correction and docking someone a day's pay.
        Assert.Equal(AttendanceStatus.Present, day.Status);
        Assert.Equal(AttendanceSource.Manual, day.Source);
        Assert.Equal(new TimeOnly(9, 0), day.FirstIn);
    }

    private sealed class TestUser : ICurrentUserService
    {
        public string? UserId => "test";
        public string? UserName => "test";
        public string? IpAddress => null;
        public string? Browser => null;
        public bool HasPermission(string permission) => true;
    }

    /// <summary>The rebuild path never reaches the terminal.</summary>
    private sealed class UnusedClient : IZkDeviceClient
    {
        public Task<ZkDeviceInfo> GetInfoAsync(string h, int p, int k, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ZkAttendanceRecord>> GetAttendanceAsync(
            string h, int p, int k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZkUser>> GetUsersAsync(
            string h, int p, int k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAttendanceAsync(string h, int p, int k, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
