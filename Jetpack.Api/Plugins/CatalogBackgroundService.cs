using Jetpack.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Plugins;

/// <summary>Periodically rescans storage and persists download counters.</summary>
public sealed class CatalogBackgroundService : BackgroundService {
  private static readonly TimeSpan S_RETRY_INTERVAL_ = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan S_STATS_INTERVAL_ = TimeSpan.FromSeconds(60);
  private readonly PluginCatalog catalog_;
  private readonly ILogger<CatalogBackgroundService> logger_;
  private readonly IOptions<JetpackOptions> options_;
  private readonly DownloadStatistics statistics_;

  public CatalogBackgroundService(PluginCatalog catalog,
                                  DownloadStatistics statistics,
                                  IOptions<JetpackOptions> options,
                                  ILogger<CatalogBackgroundService> logger) {
    catalog_ = catalog;
    statistics_ = statistics;
    options_ = options;
    logger_ = logger;
  }

  public override async Task StopAsync(CancellationToken ct) {
    await base.StopAsync(ct);
    await statistics_.FlushAsync(ct);
  }

  protected override async Task ExecuteAsync(CancellationToken ct) {
    DateTimeOffset last_refresh = DateTimeOffset.UtcNow;
    DateTimeOffset last_flush = DateTimeOffset.UtcNow;
    using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
    while (await timer.WaitForNextTickAsync(ct)) {
      DateTimeOffset now = DateTimeOffset.UtcNow;
      int interval = options_.Value.RefreshIntervalSeconds;
      TimeSpan due = catalog_.IsLoaded
                       ? TimeSpan.FromSeconds(interval)
                       : S_RETRY_INTERVAL_;
      if ((interval > 0 || !catalog_.IsLoaded) && now - last_refresh >= due) {
        last_refresh = now;
        try {
          await catalog_.RefreshAsync(ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          logger_.LogError(ex, "Refreshing the plugin catalog failed");
        }
      }

      if (now - last_flush >= S_STATS_INTERVAL_) {
        last_flush = now;
        await statistics_.FlushAsync(ct);
      }
    }
  }
}
