using System.Security.Cryptography;
using Jetpack.Api.Configuration;
using Jetpack.Api.Storage;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Plugins;

public enum UploadStatus {
  CREATED,
  REPLACED,
  CONFLICT,
  INVALID,
  TOO_LARGE
}

public sealed record UploadOutcome(UploadStatus Status, string? FileName,
                                   PluginRelease? Release, string? Error);

public sealed class PayloadTooLargeException(string message) : Exception(message);

/// <summary>Ingests uploaded plugin archives and deletes releases.</summary>
public sealed class PluginUploadService {
  private const int C_BUFFER_SIZE_ = 1024 * 1024;
  private readonly PluginCatalog catalog_;
  private readonly ILogger<PluginUploadService> logger_;
  private readonly IOptions<JetpackOptions> options_;
  private readonly IBlobStore store_;

  public PluginUploadService(IBlobStore store, PluginCatalog catalog,
                             IOptions<JetpackOptions> options,
                             ILogger<PluginUploadService> logger) {
    store_ = store;
    catalog_ = catalog;
    options_ = options;
    logger_ = logger;
  }

  /// <param name="content">The raw plugin archive.</param>
  /// <param name="file_name">Original file name, if known.</param>
  /// <param name="overwrite">Replace an existing release with the same id and version.</param>
  /// <param name="tags">Extra Marketplace tags (e.g. "Languages").</param>
  public async Task<UploadOutcome> UploadAsync(Stream content, string? file_name,
                                               bool overwrite,
                                               IReadOnlyCollection<string> tags,
                                               CancellationToken ct) {
    string temp_dir = Path.Combine(Path.GetTempPath(), "jetpack-uploads");
    Directory.CreateDirectory(temp_dir);
    string temp_path = Path.Combine(temp_dir, $"{Guid.NewGuid():N}.upload");
    try {
      (long size, string sha256) = await SpoolAsync(content, temp_path, ct);
      if (size == 0) {
        return new UploadOutcome(
          UploadStatus.INVALID,
          file_name,
          null,
          "The upload is empty."
        );
      }

      InspectedPlugin inspected;
      try {
        inspected = await Task.Run(
                      () => PluginArchiveInspector.Inspect(temp_path),
                      ct
                    );
      } catch (InvalidPluginException ex) {
        logger_.LogWarning(
          "Rejected upload {FileName}: {Reason}",
          file_name,
          ex.Message
        );
        return new UploadOutcome(
          UploadStatus.INVALID,
          file_name,
          null,
          ex.Message
        );
      }

      return await catalog_.MutateAsync(
               mutation => CommitAsync(
                 mutation,
                 inspected,
                 temp_path,
                 size,
                 sha256,
                 file_name,
                 overwrite,
                 tags,
                 ct
               ),
               ct
             );
    } catch (PayloadTooLargeException ex) {
      return new UploadOutcome(
        UploadStatus.TOO_LARGE,
        file_name,
        null,
        ex.Message
      );
    } finally {
      TryDelete(temp_path);
    }
  }

  /// <summary>Deletes one version, or every version when <paramref name="version" /> is null.</summary>
  /// <returns>The deleted releases (empty when nothing matched).</returns>
  public async Task<IReadOnlyList<PluginRelease>> DeleteAsync(string plugin_id,
    string? version, CancellationToken ct) {
    return await catalog_.MutateAsync<IReadOnlyList<PluginRelease>>(
             async mutation => {
               PluginEntry? plugin = mutation.Snapshot.FindPlugin(plugin_id);
               if (plugin == null) {
                 return [];
               }

               List<PluginRelease> deleted = new();
               foreach (ReleaseEntry release in plugin.Releases.Where(
                          r => version == null || r.Release.Version == version
                        )) {
                 // Remove the commit marker first so a partial delete can never
                 // leave a release pointing at a missing artifact.
                 await store_.DeleteAsync(release.Release.ReleaseKey, ct);
                 mutation.Remove(release.Release);
                 await DeleteQuietlyAsync(release.Release.ArtifactKey, ct);
                 await DeleteQuietlyAsync(release.Release.IconKey, ct);
                 await DeleteQuietlyAsync(release.Release.DarkIconKey, ct);
                 deleted.Add(release.Release);
                 logger_.LogInformation(
                   "Deleted {PluginId} {Version}",
                   release.Release.PluginId,
                   release.Release.Version
                 );
               }

               return deleted;
             },
             ct
           );
  }

  private async Task<UploadOutcome> CommitAsync(
    PluginCatalog.CatalogMutation mutation, InspectedPlugin inspected,
    string temp_path, long size, string sha256, string? file_name,
    bool overwrite, IReadOnlyCollection<string> tags, CancellationToken ct) {
    PluginDescriptor descriptor = inspected.Descriptor;
    ReleaseEntry? existing = mutation.Snapshot.FindRelease(
      descriptor.Id,
      descriptor.Version
    );
    if (existing != null && !overwrite) {
      return new UploadOutcome(
        UploadStatus.CONFLICT,
        file_name,
        existing.Release,
        $"{descriptor.Id} {descriptor.Version} already exists. " +
        "Upload with ?force=true to replace it."
      );
    }

    PluginRelease release = new() {
      PluginId = descriptor.Id,
      Name = descriptor.Name,
      Version = descriptor.Version,
      Vendor = descriptor.Vendor,
      VendorUrl = descriptor.VendorUrl,
      VendorEmail = descriptor.VendorEmail,
      Description = descriptor.Description,
      ChangeNotes = descriptor.ChangeNotes,
      SinceBuild = descriptor.SinceBuild,
      UntilBuild = descriptor.UntilBuild,
      Url = descriptor.Url,
      ProductCode = descriptor.ProductCode,
      Tags = tags.Concat(descriptor.Category is { } c ? [c] : [])
                 .Select(t => t.Trim())
                 .Where(t => t.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList(),
      Dependencies = descriptor.Dependencies.ToList(),
      OptionalDependencies = descriptor.OptionalDependencies.ToList(),
      Modules = descriptor.Modules.ToList(),
      FileName = StorageLayout.ToArtifactFileName(
        file_name,
        descriptor,
        inspected.Extension
      ),
      Size = size,
      Sha256 = sha256,
      UploadedAt = DateTimeOffset.UtcNow,
      HasIcon = inspected.Icon != null,
      HasDarkIcon = inspected.DarkIcon != null,
      Folder = existing?.Release.Folder ??
               StorageLayout.FolderFor(descriptor.Id, descriptor.Version)
    };

    await using (FileStream artifact = new(
                   temp_path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   C_BUFFER_SIZE_,
                   FileOptions.Asynchronous | FileOptions.SequentialScan
                 )) {
      await store_.PutAsync(
        release.ArtifactKey,
        artifact,
        size,
        release.IsJar ? "application/java-archive" : "application/zip",
        ct
      );
    }

    await PutOrDeleteAsync(release.IconKey, inspected.Icon, ct);
    await PutOrDeleteAsync(release.DarkIconKey, inspected.DarkIcon, ct);
    // release.json is written last: it is the commit marker.
    await store_.PutBytesAsync(
      release.ReleaseKey,
      PluginCatalog.Serialize(release),
      "application/json",
      ct
    );
    await mutation.PutAsync(release, ct);
    if (existing != null && existing.Release.FileName != release.FileName) {
      await DeleteQuietlyAsync(existing.Release.ArtifactKey, ct);
    }

    logger_.LogInformation(
      "{Action} {PluginId} {Version} ({Kind}, {Size} bytes, " +
      "since {Since} until {Until})",
      existing == null ? "Added" : "Replaced",
      release.PluginId,
      release.Version,
      release.IsJar ? "jar" : "zip",
      release.Size,
      release.SinceBuild ?? "-",
      release.UntilBuild ?? "-"
    );
    return new UploadOutcome(
      existing == null ? UploadStatus.CREATED : UploadStatus.REPLACED,
      file_name,
      release,
      null
    );
  }

  private async Task<(long Size, string Sha256)> SpoolAsync(Stream content,
    string path, CancellationToken ct) {
    long limit = options_.Value.MaxUploadBytes;
    using IncrementalHash hash =
      IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[C_BUFFER_SIZE_];
    long total = 0;
    await using FileStream output = new(
      path,
      FileMode.CreateNew,
      FileAccess.Write,
      FileShare.None,
      C_BUFFER_SIZE_,
      FileOptions.Asynchronous
    );
    int read;
    while ((read = await content.ReadAsync(buffer, ct)) > 0) {
      total += read;
      if (total > limit) {
        throw new PayloadTooLargeException(
          $"Plugin archives larger than {limit} bytes are not accepted " +
          "(Jetpack:MaxUploadBytes)."
        );
      }

      hash.AppendData(buffer, 0, read);
      await output.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
  }

  private async Task PutOrDeleteAsync(string key, byte[]? content,
                                      CancellationToken ct) {
    if (content != null) {
      await store_.PutBytesAsync(key, content, "image/svg+xml", ct);
    } else {
      await DeleteQuietlyAsync(key, ct);
    }
  }

  private async Task DeleteQuietlyAsync(string key, CancellationToken ct) {
    try {
      await store_.DeleteAsync(key, ct);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      logger_.LogDebug(ex, "Could not delete {Key}", key);
    }
  }

  private static void TryDelete(string path) {
    try {
      File.Delete(path);
    } catch (IOException) {
      // Best effort.
    }
  }
}
