using System.Collections.Concurrent;
using System.Text.Json;
using Jetpack.Api.Storage;

namespace Jetpack.Api.Plugins;

/// <summary>
///   Per-plugin download counters, shown in the IDE and used for the
///   "Top Downloads" ordering. Persisted periodically; exact counts across
///   several instances are not a goal.
/// </summary>
public sealed class DownloadStatistics {
  private readonly ConcurrentDictionary<string, long> counts_ =
    new(StringComparer.Ordinal);

  private readonly ILogger<DownloadStatistics> logger_;
  private readonly IBlobStore store_;
  private int dirty_;

  public DownloadStatistics(IBlobStore store,
                            ILogger<DownloadStatistics> logger) {
    store_ = store;
    logger_ = logger;
  }

  public long Get(string plugin_id) {
    return counts_.GetValueOrDefault(plugin_id);
  }

  public void Increment(string plugin_id) {
    counts_.AddOrUpdate(plugin_id, 1, (_, count) => count + 1);
    Interlocked.Exchange(ref dirty_, 1);
  }

  public async Task LoadAsync(CancellationToken ct) {
    try {
      byte[]? json = await store_.ReadAllBytesAsync(
                       StorageLayout.C_DOWNLOAD_STATS_KEY,
                       ct
                     );
      if (json == null) {
        return;
      }

      Dictionary<string, long>? stored =
        JsonSerializer.Deserialize<Dictionary<string, long>>(json);
      foreach ((string id, long count) in stored ?? new()) {
        counts_.AddOrUpdate(id, count, (_, current) => Math.Max(current, count));
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      logger_.LogWarning(ex, "Could not load download statistics");
    }
  }

  public async Task FlushAsync(CancellationToken ct) {
    if (Interlocked.Exchange(ref dirty_, 0) == 0) {
      return;
    }

    try {
      byte[] json = JsonSerializer.SerializeToUtf8Bytes(
        counts_.ToDictionary(p => p.Key, p => p.Value)
      );
      await store_.PutBytesAsync(
        StorageLayout.C_DOWNLOAD_STATS_KEY,
        json,
        "application/json",
        ct
      );
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      Interlocked.Exchange(ref dirty_, 1);
      logger_.LogWarning(ex, "Could not save download statistics");
    }
  }
}
