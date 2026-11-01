using System.Security.Cryptography;
using System.Text;

namespace Jetpack.Api.Plugins;

/// <summary>
///   One uploaded plugin version. Persisted as <c>release.json</c> next to the
///   artifact; the presence of that file is what makes a release visible.
/// </summary>
public sealed class PluginRelease {
  public int SchemaVersion { get; set; } = 1;
  public required string PluginId { get; set; }
  public required string Name { get; set; }
  public required string Version { get; set; }
  public string? Vendor { get; set; }
  public string? VendorUrl { get; set; }
  public string? VendorEmail { get; set; }
  public string? Description { get; set; }
  public string? ChangeNotes { get; set; }
  public string? SinceBuild { get; set; }
  public string? UntilBuild { get; set; }
  public string? Url { get; set; }
  public string? ProductCode { get; set; }
  public List<string> Tags { get; set; } = new();
  public List<string> Dependencies { get; set; } = new();
  public List<string> OptionalDependencies { get; set; } = new();
  public List<string> Modules { get; set; } = new();

  /// <summary>Artifact file name, always ending in <c>.jar</c> or <c>.zip</c>.</summary>
  public required string FileName { get; set; }

  public long Size { get; set; }
  public required string Sha256 { get; set; }
  public DateTimeOffset UploadedAt { get; set; }
  public bool HasIcon { get; set; }
  public bool HasDarkIcon { get; set; }

  /// <summary>Storage folder holding this release, e.g. <c>plugins/a.b/1.0</c>.</summary>
  public string Folder { get; set; } = "";

  public string ArtifactKey => $"{Folder}/{FileName}";
  public string IconKey => $"{Folder}/{StorageLayout.C_ICON_FILE}";
  public string DarkIconKey => $"{Folder}/{StorageLayout.C_DARK_ICON_FILE}";
  public string ReleaseKey => $"{Folder}/{StorageLayout.C_RELEASE_FILE}";

  public bool IsJar => FileName.EndsWith(
    ".jar",
    StringComparison.OrdinalIgnoreCase
  );
}

/// <summary>
///   Storage layout:
///   <code>
///   plugins/{plugin-id}/{version}/release.json
///   plugins/{plugin-id}/{version}/{artifact}.zip|.jar
///   plugins/{plugin-id}/{version}/pluginIcon.svg, pluginIcon_dark.svg
///   static/brokenPlugins.json
///   stats/downloads.json
///   </code>
/// </summary>
public static class StorageLayout {
  public const string C_PLUGINS_PREFIX = "plugins/";
  public const string C_RELEASE_FILE = "release.json";
  public const string C_ICON_FILE = "pluginIcon.svg";
  public const string C_DARK_ICON_FILE = "pluginIcon_dark.svg";
  public const string C_BROKEN_PLUGINS_KEY = "static/brokenPlugins.json";
  public const string C_DOWNLOAD_STATS_KEY = "stats/downloads.json";

  public static string FolderFor(string plugin_id, string version) {
    return $"plugins/{ToSegment(plugin_id)}/{ToSegment(version)}";
  }

  /// <summary>
  ///   Turns an arbitrary plugin id or version into a safe path segment. When
  ///   characters had to be replaced a short hash keeps the result unique.
  /// </summary>
  public static string ToSegment(string value) {
    StringBuilder builder = new(value.Length);
    foreach (char c in value) {
      builder.Append(IsSafe(c) ? c : '_');
    }

    string segment = builder.ToString();
    bool changed = segment != value ||
                   segment.StartsWith('.') ||
                   segment.Length > 96;
    if (!changed) {
      return segment;
    }

    if (segment.Length > 96) {
      segment = segment[..96];
    }

    string hash = Convert.ToHexString(
                           SHA256.HashData(Encoding.UTF8.GetBytes(value))
                         )[..8]
                         .ToLowerInvariant();
    return $"{segment.TrimStart('.')}~{hash}";
  }

  /// <summary>Makes an uploaded file name safe and gives it the right extension.</summary>
  public static string ToArtifactFileName(string? original_name,
                                          PluginDescriptor descriptor,
                                          string extension) {
    string? base_name = null;
    if (!string.IsNullOrWhiteSpace(original_name)) {
      string file_name = Path.GetFileName(original_name.Replace('\\', '/'));
      base_name = Path.GetFileNameWithoutExtension(file_name);
    }

    if (string.IsNullOrWhiteSpace(base_name)) {
      base_name = $"{descriptor.Name}-{descriptor.Version}";
    }

    StringBuilder builder = new(base_name.Length);
    foreach (char c in base_name.Trim()) {
      builder.Append(
        IsSafe(c) ? c : char.IsWhiteSpace(c) ? '-' : '_'
      );
    }

    string safe = builder.ToString().Trim('.', '-', '_');
    if (safe.Length == 0) {
      safe = "plugin";
    }

    if (safe.Length > 150) {
      safe = safe[..150];
    }

    return safe + extension;
  }

  private static bool IsSafe(char c) {
    return char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';
  }
}
