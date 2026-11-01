using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jetpack.Api.Compatibility;

namespace Jetpack.Api.Plugins;

public sealed class ReleaseEntry {
  public required PluginRelease Release { get; init; }
  public required int UpdateId { get; init; }
  public PluginEntry Plugin { get; internal set; } = null!;

  public bool IsCompatibleWith(BuildNumber? build) {
    return build == null ||
           BuildNumber.IsCompatible(
             Release.SinceBuild,
             Release.UntilBuild,
             build
           );
  }
}

public sealed partial class PluginEntry {
  public required string Id { get; init; }
  public required int NumericId { get; init; }

  /// <summary>All releases, newest version first (IntelliJ version ordering).</summary>
  public required IReadOnlyList<ReleaseEntry> Releases { get; init; }

  public ReleaseEntry Latest => Releases[0];
  public string Name => Latest.Release.Name;
  public string? Vendor => Latest.Release.Vendor;

  public IReadOnlyList<string> Tags =>
    Releases.SelectMany(r => r.Release.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

  /// <summary>Lower-cased plain text of the description, used for search.</summary>
  public string DescriptionText { get; private init; } = "";

  public DateTimeOffset LastUpdated =>
    Releases.Max(r => r.Release.UploadedAt);

  /// <summary>Newest release the given IDE build can load (newest overall when no build is given).</summary>
  public ReleaseEntry? LatestCompatible(BuildNumber? build) {
    return Releases.FirstOrDefault(r => r.IsCompatibleWith(build));
  }

  internal static string ToPlainText(string? html) {
    if (string.IsNullOrEmpty(html)) {
      return "";
    }

    string text = HtmlComment().Replace(html, " ");
    text = WebUtility.HtmlDecode(HtmlTag().Replace(text, " "));
    return Whitespace().Replace(text, " ").Trim();
  }

  internal static PluginEntry Create(string id, int numeric_id,
                                     IReadOnlyList<ReleaseEntry> releases) {
    PluginEntry entry = new() {
      Id = id,
      NumericId = numeric_id,
      Releases = releases,
      DescriptionText = ToPlainText(releases[0].Release.Description)
        .ToLowerInvariant()
    };
    foreach (ReleaseEntry release in releases) {
      release.Plugin = entry;
    }

    return entry;
  }

  [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
  private static partial Regex HtmlComment();

  [GeneratedRegex("<[^>]*>")]
  private static partial Regex HtmlTag();

  [GeneratedRegex(@"\s+")]
  private static partial Regex Whitespace();
}

/// <summary>Immutable, consistent view of all releases; swapped atomically on change.</summary>
public sealed class CatalogSnapshot {
  public static readonly CatalogSnapshot S_EMPTY = Build([]);

  private readonly Dictionary<string, ReleaseEntry> legacy_files_;
  private readonly Dictionary<string, List<PluginEntry>> modules_;
  private readonly Dictionary<int, PluginEntry> by_numeric_id_;
  private readonly Dictionary<string, PluginEntry> by_id_;
  private readonly Dictionary<int, ReleaseEntry> by_update_id_;

  private CatalogSnapshot(List<PluginEntry> plugins) {
    Plugins = plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Id, StringComparer.Ordinal)
                     .ToList();
    by_id_ = plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
    by_numeric_id_ = plugins.ToDictionary(p => p.NumericId);
    by_update_id_ = plugins.SelectMany(p => p.Releases)
                           .ToDictionary(r => r.UpdateId);
    modules_ = new Dictionary<string, List<PluginEntry>>(StringComparer.Ordinal);
    legacy_files_ = new Dictionary<string, ReleaseEntry>(StringComparer.Ordinal);
    foreach (PluginEntry plugin in Plugins) {
      foreach (string module in plugin.Releases
                                      .SelectMany(r => r.Release.Modules)
                                      .Distinct(StringComparer.Ordinal)) {
        if (!modules_.TryGetValue(module, out List<PluginEntry>? list)) {
          modules_[module] = list = new List<PluginEntry>();
        }

        list.Add(plugin);
      }

      foreach (ReleaseEntry release in plugin.Releases) {
        // File names used by the previous Jetpack version, so download URLs
        // already cached by IDEs keep working.
        legacy_files_.TryAdd(
          $"{release.Release.PluginId}-{release.Release.Version}.zip",
          release
        );
      }
    }

    ReleaseCount = by_update_id_.Count;
    ETag = ComputeETag(Plugins);
  }

  public IReadOnlyList<PluginEntry> Plugins { get; }
  public int ReleaseCount { get; }

  /// <summary>Changes whenever any release is added, replaced or removed.</summary>
  public string ETag { get; }

  public PluginEntry? FindPlugin(string id) {
    return by_id_.GetValueOrDefault(id);
  }

  public PluginEntry? FindPlugin(int numeric_id) {
    return by_numeric_id_.GetValueOrDefault(numeric_id);
  }

  public ReleaseEntry? FindUpdate(int update_id) {
    return by_update_id_.GetValueOrDefault(update_id);
  }

  public ReleaseEntry? FindRelease(string id, string version) {
    return FindPlugin(id)
           ?.Releases
           .FirstOrDefault(r => r.Release.Version == version);
  }

  public ReleaseEntry? FindLegacyFile(string file_name) {
    return legacy_files_.GetValueOrDefault(file_name);
  }

  /// <summary>Plugins declaring <c>&lt;module value="..."/&gt;</c>, or with that id.</summary>
  public IReadOnlyList<PluginEntry> FindByModule(string module) {
    List<PluginEntry> result = modules_.GetValueOrDefault(module) ?? new();
    PluginEntry? by_id = FindPlugin(module);
    if (by_id != null && !result.Contains(by_id)) {
      result = [by_id, ..result];
    }

    return result;
  }

  public static CatalogSnapshot Build(IEnumerable<PluginRelease> releases) {
    // Deterministic numeric ids (the IDE expects Marketplace-style integer
    // plugin and update ids). Stable across restarts and instances because they
    // derive from the plugin id / version; collisions are resolved by probing
    // in a fixed order.
    HashSet<int> used_plugin_ids = new();
    HashSet<int> used_update_ids = new();
    List<PluginEntry> plugins = new();
    foreach (IGrouping<string, PluginRelease> group in releases
                                                       .GroupBy(
                                                         r => r.PluginId,
                                                         StringComparer.Ordinal
                                                       )
                                                       .OrderBy(
                                                         g => g.Key,
                                                         StringComparer.Ordinal
                                                       )) {
      int plugin_id = AllocateId($"plugin:{group.Key}", used_plugin_ids);
      List<ReleaseEntry> entries = group
                                   .OrderBy(
                                     r => r.Version,
                                     StringComparer.Ordinal
                                   )
                                   .Select(
                                     r => new ReleaseEntry {
                                       Release = r,
                                       UpdateId = AllocateId(
                                         $"update:{r.PluginId}\n{r.Version}",
                                         used_update_ids
                                       )
                                     }
                                   )
                                   .OrderByDescending(
                                     r => r.Release.Version,
                                     VersionComparator.S_INSTANCE
                                   )
                                   .ThenByDescending(r => r.Release.UploadedAt)
                                   .ToList();
      plugins.Add(PluginEntry.Create(group.Key, plugin_id, entries));
    }

    return new CatalogSnapshot(plugins);
  }

  private static int AllocateId(string key, HashSet<int> used) {
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    // Keep ids in a plausible, positive, 31-bit range.
    int id = (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 2_000_000_000u) +
             100_000;
    while (!used.Add(id)) {
      id = id == int.MaxValue ? 100_000 : id + 1;
    }

    return id;
  }

  private static string ComputeETag(IEnumerable<PluginEntry> plugins) {
    StringBuilder builder = new();
    foreach (ReleaseEntry release in plugins.SelectMany(p => p.Releases)
                                            .OrderBy(r => r.UpdateId)) {
      builder.Append(release.UpdateId)
             .Append(':')
             .Append(release.Release.Sha256)
             .Append(':')
             .Append(string.Join(',', release.Release.Tags))
             .Append('\n');
    }

    string hash = Convert.ToHexString(
      SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))
    );
    return $"\"{hash[..32].ToLowerInvariant()}\"";
  }
}
