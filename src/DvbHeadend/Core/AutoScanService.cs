using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>
/// Rescans the channel list on its own every few days, in the small hours and
/// only while both tuners are free, so the line-up follows Freeview changes.
/// </summary>
public sealed class AutoScanService(Scanner scanner, StateStore state, TunerManager tuners,
    IOptions<HeadendOptions> options, ILogger<AutoScanService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var settings = options.Value;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stop);
                if (IsDue(settings, state.LastScanUtc, DateTime.UtcNow, DateTime.Now.Hour) && tuners.AllTunersIdle)
                {
                    log.LogInformation("Starting the automatic channel rescan");
                    scanner.Start();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static bool IsDue(HeadendOptions settings, DateTime? lastScanUtc, DateTime nowUtc, int localHour)
    {
        if (settings.AutoScanIntervalDays <= 0 || localHour != settings.AutoScanHour)
        {
            return false;
        }
        // Half a day of slack so a scan that ran at 04:50 last week still counts as due at 04:00.
        return lastScanUtc is null || nowUtc - lastScanUtc.Value >= TimeSpan.FromDays(settings.AutoScanIntervalDays - 0.5);
    }
}
