namespace Jetpack.Api.Configuration;

/// <summary>General server settings (configuration section <c>Jetpack</c>).</summary>
public sealed class JetpackOptions {
  public const string C_SECTION = "Jetpack";

  /// <summary>
  ///   Key required (via <c>X-Api-Key</c> or <c>Authorization: Bearer</c>) for
  ///   uploads, deletes and other write operations.
  /// </summary>
  public string? ApiKey { get; set; }

  /// <summary>
  ///   Allows write operations without an API key when none is configured.
  ///   Only intended for throw-away test setups.
  /// </summary>
  public bool AllowAnonymousUploads { get; set; }

  /// <summary>
  ///   Optional externally visible base URL (e.g. <c>https://plugins.corp</c>).
  ///   Only used to emit absolute download URLs in the legacy
  ///   <c>updatePlugins.xml</c>; everything else uses relative URLs, so this is
  ///   normally not needed.
  /// </summary>
  public string? PublicUrl { get; set; }

  /// <summary>Maximum size of a single uploaded plugin archive.</summary>
  public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

  /// <summary>
  ///   How often (seconds) storage is rescanned for releases added or removed
  ///   by other instances or by hand. <c>0</c> disables rescanning.
  /// </summary>
  public int RefreshIntervalSeconds { get; set; } = 60;

  /// <summary>
  ///   Plugin IDs shown in the IDE's "Staff Picks" section, in order. When
  ///   empty all plugins are shown there.
  /// </summary>
  public List<string> FeaturedPlugins { get; set; } = new();

  /// <summary>Logs every request (method, path, status, IDE user agent).</summary>
  public bool LogRequests { get; set; } = true;
}

public enum StorageProvider {
  FILE_SYSTEM,
  S3
}

/// <summary>Storage settings (configuration section <c>Storage</c>).</summary>
public sealed class StorageOptions {
  public const string C_SECTION = "Storage";

  /// <summary><c>FileSystem</c> (default) or <c>S3</c> (MinIO etc.).</summary>
  public string Provider { get; set; } = "FileSystem";

  /// <summary>Root directory for the file system provider.</summary>
  public string Path { get; set; } = "data";

  public S3StorageOptions S3 { get; set; } = new();

  public StorageProvider GetProvider() {
    string normalized = Provider.Replace("_", "")
                                .Replace("-", "")
                                .Trim()
                                .ToLowerInvariant();
    return normalized switch {
      "filesystem" or "file" or "fs" or "disk" => StorageProvider.FILE_SYSTEM,
      "s3" or "minio" => StorageProvider.S3,
      _ => throw new InvalidOperationException(
        $"Unknown storage provider '{Provider}'. Use 'FileSystem' or 'S3'."
      )
    };
  }
}

public sealed class S3StorageOptions {
  /// <summary>Host and port, e.g. <c>minio.corp:9000</c> (no scheme).</summary>
  public string? Endpoint { get; set; }

  public string? AccessKey { get; set; }
  public string? SecretKey { get; set; }

  /// <summary>Use HTTPS towards the S3 endpoint.</summary>
  public bool Secure { get; set; }

  public string? Region { get; set; }
  public string Bucket { get; set; } = "jetpack";

  /// <summary>Key prefix for every object written by Jetpack.</summary>
  public string Prefix { get; set; } = "jetpack";
}
