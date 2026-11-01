namespace Jetpack.Api.Storage;

public sealed record BlobInfo(string Key, long Length, DateTimeOffset LastModified);

/// <summary>
///   Minimal key/value blob storage. Keys are relative, <c>/</c>-separated paths
///   such as <c>plugins/org.example/1.0.0/release.json</c>.
/// </summary>
public interface IBlobStore {
  /// <summary>Human readable location, used in logs and the health endpoint.</summary>
  string Description { get; }

  Task InitializeAsync(CancellationToken ct);

  Task PutAsync(string key, Stream content, long length, string content_type,
                CancellationToken ct);

  Task<BlobInfo?> GetInfoAsync(string key, CancellationToken ct);

  /// <summary>Streams a blob into <paramref name="destination" />.</summary>
  /// <returns><c>false</c> when the blob does not exist.</returns>
  Task<bool> CopyToAsync(string key, Stream destination, CancellationToken ct);

  /// <summary>Lists all blobs whose key starts with <paramref name="prefix" />.</summary>
  IAsyncEnumerable<BlobInfo> ListAsync(string prefix, CancellationToken ct);

  Task DeleteAsync(string key, CancellationToken ct);
}

public static class BlobStoreExtensions {
  public static async Task PutBytesAsync(this IBlobStore store, string key,
                                         byte[] content, string content_type,
                                         CancellationToken ct) {
    using MemoryStream stream = new(content, false);
    await store.PutAsync(key, stream, content.Length, content_type, ct);
  }

  public static async Task<byte[]?> ReadAllBytesAsync(this IBlobStore store,
    string key, CancellationToken ct) {
    using MemoryStream stream = new();
    return await store.CopyToAsync(key, stream, ct) ? stream.ToArray() : null;
  }

  /// <summary>Rejects keys that could escape the storage root.</summary>
  public static void ValidateKey(string key) {
    if (string.IsNullOrEmpty(key) ||
        key.StartsWith('/') ||
        key.Contains('\\') ||
        key.Contains('\0')) {
      throw new ArgumentException($"Invalid storage key '{key}'.", nameof(key));
    }

    foreach (string segment in key.Split('/')) {
      if (segment.Length == 0 || segment == "." || segment == "..") {
        throw new ArgumentException(
          $"Invalid storage key '{key}'.",
          nameof(key)
        );
      }
    }
  }
}
