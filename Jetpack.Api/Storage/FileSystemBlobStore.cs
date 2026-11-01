using System.Runtime.CompilerServices;

namespace Jetpack.Api.Storage;

/// <summary>Stores blobs as plain files below a root directory.</summary>
public sealed class FileSystemBlobStore : IBlobStore {
  private const string C_TEMP_MARKER_ = ".jetpack-tmp-";
  private const int C_BUFFER_SIZE_ = 1024 * 1024;
  private readonly string root_;

  public FileSystemBlobStore(string root) {
    root_ = Path.GetFullPath(root);
  }

  public string Description => $"file system ({root_})";

  public Task InitializeAsync(CancellationToken ct) {
    Directory.CreateDirectory(root_);
    // Clean up leftovers of interrupted writes.
    foreach (string file in Directory.EnumerateFiles(
               root_,
               $"*{C_TEMP_MARKER_}*",
               SearchOption.AllDirectories
             )) {
      TryDelete(file);
    }

    return Task.CompletedTask;
  }

  public async Task PutAsync(string key, Stream content, long length,
                             string content_type, CancellationToken ct) {
    string path = ResolvePath(key);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    // Write to a temp file first so readers never observe partial content.
    string temp_path = $"{path}{C_TEMP_MARKER_}{Guid.NewGuid():N}";
    try {
      await using (FileStream output = new(
                     temp_path,
                     FileMode.CreateNew,
                     FileAccess.Write,
                     FileShare.None,
                     C_BUFFER_SIZE_,
                     FileOptions.Asynchronous
                   )) {
        await content.CopyToAsync(output, C_BUFFER_SIZE_, ct);
      }

      File.Move(temp_path, path, true);
    } catch {
      TryDelete(temp_path);
      throw;
    }
  }

  public Task<BlobInfo?> GetInfoAsync(string key, CancellationToken ct) {
    FileInfo info = new(ResolvePath(key));
    return Task.FromResult(
      info.Exists
        ? new BlobInfo(key, info.Length, info.LastWriteTimeUtc)
        : null
    );
  }

  public async Task<bool> CopyToAsync(string key, Stream destination,
                                      CancellationToken ct) {
    FileStream input;
    try {
      input = new FileStream(
        ResolvePath(key),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        C_BUFFER_SIZE_,
        FileOptions.Asynchronous | FileOptions.SequentialScan
      );
    } catch (Exception ex) when (ex is FileNotFoundException
                                       or DirectoryNotFoundException) {
      return false;
    }

    await using (input) {
      await input.CopyToAsync(destination, C_BUFFER_SIZE_, ct);
    }

    return true;
  }

  public async IAsyncEnumerable<BlobInfo> ListAsync(string prefix,
    [EnumeratorCancellation] CancellationToken ct) {
    if (!Directory.Exists(root_)) {
      yield break;
    }

    // Narrow the walk to the deepest directory named by the prefix.
    int last_slash = prefix.LastIndexOf('/');
    string start = last_slash > 0
                     ? Path.Combine(root_, prefix[..last_slash])
                     : root_;
    if (!Directory.Exists(start)) {
      yield break;
    }

    foreach (string file in Directory.EnumerateFiles(
               start,
               "*",
               SearchOption.AllDirectories
             )) {
      ct.ThrowIfCancellationRequested();
      if (file.Contains(C_TEMP_MARKER_)) {
        continue;
      }

      string key = Path.GetRelativePath(root_, file)
                       .Replace(Path.DirectorySeparatorChar, '/');
      if (!key.StartsWith(prefix, StringComparison.Ordinal)) {
        continue;
      }

      FileInfo info = new(file);
      if (!info.Exists) {
        continue;
      }

      yield return new BlobInfo(key, info.Length, info.LastWriteTimeUtc);
    }

    await Task.CompletedTask;
  }

  public Task DeleteAsync(string key, CancellationToken ct) {
    string path = ResolvePath(key);
    TryDelete(path);
    // Remove directories that became empty, up to the root.
    string? directory = Path.GetDirectoryName(path);
    while (directory != null &&
           directory.Length > root_.Length &&
           Directory.Exists(directory) &&
           !Directory.EnumerateFileSystemEntries(directory).Any()) {
      try {
        Directory.Delete(directory);
      } catch (IOException) {
        break;
      }

      directory = Path.GetDirectoryName(directory);
    }

    return Task.CompletedTask;
  }

  private string ResolvePath(string key) {
    BlobStoreExtensions.ValidateKey(key);
    string path = Path.GetFullPath(Path.Combine(root_, key));
    if (!path.StartsWith(root_ + Path.DirectorySeparatorChar,
                         StringComparison.Ordinal)) {
      throw new ArgumentException($"Invalid storage key '{key}'.", nameof(key));
    }

    return path;
  }

  private static void TryDelete(string path) {
    try {
      File.Delete(path);
    } catch (IOException) {
      // Best effort.
    } catch (UnauthorizedAccessException) {
      // Best effort.
    }
  }
}
