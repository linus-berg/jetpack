using System.Text.Json;
using System.Text.Json.Serialization;
using Jetpack.Api.Storage;

namespace Jetpack.Api.Plugins;

/// <summary>
///   Holds the current <see cref="CatalogSnapshot" /> and keeps it in sync with
///   storage. Storage is the single source of truth: the catalog can always be
///   rebuilt from the <c>release.json</c> files, so several instances can share
///   one bucket and files added by hand are picked up on the next refresh.
/// </summary>
public sealed class PluginCatalog {
  public static readonly JsonSerializerOptions S_JSON_OPTIONS = new() {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
  };

  private readonly ILogger<PluginCatalog> logger_;
  private readonly IBlobStore store_;
  private readonly SemaphoreSlim write_lock_ = new(1, 1);

  // release.json key -> what was loaded from it (only touched under write_lock_)
  private readonly Dictionary<string, (BlobInfo Info, PluginRelease Release)>
    loaded_ = new(StringComparer.Ordinal);

  private volatile CatalogSnapshot snapshot_ = CatalogSnapshot.S_EMPTY;

  public PluginCatalog(IBlobStore store, ILogger<PluginCatalog> logger) {
    store_ = store;
    logger_ = logger;
  }

  public CatalogSnapshot Snapshot => snapshot_;

  /// <summary><c>true</c> once storage has been read successfully at least once.</summary>
  public bool IsLoaded { get; private set; }

  /// <summary>Re-reads storage, loading new or changed releases and dropping removed ones.</summary>
  public async Task RefreshAsync(CancellationToken ct) {
    await write_lock_.WaitAsync(ct);
    try {
      Dictionary<string, BlobInfo> present = new(StringComparer.Ordinal);
      await foreach (BlobInfo info in store_.ListAsync(
                       StorageLayout.C_PLUGINS_PREFIX,
                       ct
                     )) {
        if (info.Key.EndsWith(
              "/" + StorageLayout.C_RELEASE_FILE,
              StringComparison.Ordinal
            )) {
          present[info.Key] = info;
        }
      }

      bool changed = false;
      foreach (string key in loaded_.Keys.Where(k => !present.ContainsKey(k))
                                    .ToList()) {
        loaded_.Remove(key);
        changed = true;
      }

      foreach ((string key, BlobInfo info) in present) {
        if (loaded_.TryGetValue(key, out (BlobInfo Info, PluginRelease) cached) &&
            cached.Info.Length == info.Length &&
            cached.Info.LastModified == info.LastModified) {
          continue;
        }

        PluginRelease? release = await ReadReleaseAsync(key, ct);
        if (release == null) {
          continue;
        }

        loaded_[key] = (info, release);
        changed = true;
      }

      if (changed || !IsLoaded) {
        Rebuild();
        logger_.LogInformation(
          "Catalog loaded from {Storage}: {Plugins} plugins, {Releases} releases",
          store_.Description,
          snapshot_.Plugins.Count,
          snapshot_.ReleaseCount
        );
      }

      IsLoaded = true;
    } finally {
      write_lock_.Release();
    }
  }

  /// <summary>Runs a storage mutation exclusively, so checks and writes don't race.</summary>
  public async Task<T> MutateAsync<T>(Func<CatalogMutation, Task<T>> action,
                                      CancellationToken ct) {
    await write_lock_.WaitAsync(ct);
    try {
      return await action(new CatalogMutation(this));
    } finally {
      write_lock_.Release();
    }
  }

  public static byte[] Serialize(PluginRelease release) {
    return JsonSerializer.SerializeToUtf8Bytes(release, S_JSON_OPTIONS);
  }

  private async Task<PluginRelease?> ReadReleaseAsync(string key,
    CancellationToken ct) {
    try {
      byte[]? json = await store_.ReadAllBytesAsync(key, ct);
      if (json == null) {
        return null;
      }

      PluginRelease? release =
        JsonSerializer.Deserialize<PluginRelease>(json, S_JSON_OPTIONS);
      if (release == null) {
        return null;
      }

      // The location on disk is authoritative.
      release.Folder = key[..^(StorageLayout.C_RELEASE_FILE.Length + 1)];
      return release;
    } catch (Exception ex) when (ex is JsonException or IOException) {
      logger_.LogWarning(ex, "Ignoring unreadable release file {Key}", key);
      return null;
    }
  }

  private void Rebuild() {
    snapshot_ = CatalogSnapshot.Build(loaded_.Values.Select(v => v.Release));
  }

  /// <summary>Handle given to <see cref="MutateAsync{T}" /> callbacks.</summary>
  public sealed class CatalogMutation {
    private readonly PluginCatalog catalog_;

    internal CatalogMutation(PluginCatalog catalog) {
      catalog_ = catalog;
    }

    public CatalogSnapshot Snapshot => catalog_.snapshot_;

    public async Task PutAsync(PluginRelease release, CancellationToken ct) {
      BlobInfo info = await catalog_.store_.GetInfoAsync(release.ReleaseKey, ct) ??
                      throw new IOException(
                        $"{release.ReleaseKey} vanished after writing it."
                      );
      catalog_.loaded_[release.ReleaseKey] = (info, release);
      catalog_.Rebuild();
    }

    public void Remove(PluginRelease release) {
      if (catalog_.loaded_.Remove(release.ReleaseKey)) {
        catalog_.Rebuild();
      }
    }
  }
}
