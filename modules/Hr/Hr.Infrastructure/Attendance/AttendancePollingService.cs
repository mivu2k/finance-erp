using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hr.Infrastructure.Attendance;

public class AttendancePollingOptions
{
    public const string Section = "Attendance";

    /// <summary>Turn the poller off to sync only on demand from the devices page.</summary>
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Delay before the first poll, so startup isn't competing with migrations.</summary>
    public int StartupDelaySeconds { get; set; } = 60;
}

/// <summary>
/// Pulls the terminals on a schedule. The devices hold their logs on board, so a
/// missed cycle costs nothing — the next one picks up everything since.
/// </summary>
public class AttendancePollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<AttendancePollingOptions> options,
    ILogger<AttendancePollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Attendance polling is disabled; sync on demand only");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.StartupDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Attendance polling every {Minutes} minute(s)", interval.TotalMinutes);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IAttendanceSyncService>();

                var results = await sync.SyncAllAsync(stoppingToken);

                foreach (var result in results.Where(r => !r.Succeeded))
                    logger.LogWarning("Attendance sync failed for {Device}: {Message}",
                        result.DeviceName, result.Message);

                var stored = results.Sum(r => r.PunchesNew);
                if (stored > 0)
                    logger.LogInformation(
                        "Attendance sync stored {Count} new punch(es) across {Devices} device(s)",
                        stored, results.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad cycle kill the poller for the life of the process.
                logger.LogError(ex, "Attendance polling cycle failed");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
