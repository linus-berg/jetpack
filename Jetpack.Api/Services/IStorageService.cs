namespace Jetpack.Api.Services;

/// <summary>
/// Provides an abstraction for file storage operations, allowing interaction with object storage services.
/// </summary>
public interface IStorageService {
  /// <summary>
  /// Uploads a file to the specified bucket asynchronously.
  /// </summary>
  /// <param name="bucket_name">The name of the bucket where the file will be stored.</param>
  /// <param name="object_name">The unique name (key) of the object within the bucket.</param>
  /// <param name="data">The stream containing the file data to upload.</param>
  /// <param name="content_type">The MIME type of the content.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  Task UploadFileAsync(string bucket_name, string object_name, Stream data,
                       string content_type);

  /// <summary>
  /// Retrieves a file from the specified bucket asynchronously.
  /// </summary>
  /// <param name="bucket_name">The name of the bucket containing the file.</param>
  /// <param name="object_name">The unique name (key) of the object to retrieve.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the file data as a stream.</returns>
  Task<Stream> GetFileAsync(string bucket_name, string object_name);

  /// <summary>
  /// Checks if a file exists in the specified bucket asynchronously.
  /// </summary>
  /// <param name="bucket_name">The name of the bucket to check.</param>
  /// <param name="object_name">The unique name (key) of the object to check for existence.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains true if the file exists; otherwise, false.</returns>
  Task<bool> FileExistsAsync(string bucket_name, string object_name);

  /// <summary>
  /// Ensures that the specified bucket exists, creating it if necessary asynchronously.
  /// </summary>
  /// <param name="bucket_name">The name of the bucket to ensure exists.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  Task EnsureBucketExistsAsync(string bucket_name);

  /// <summary>
  /// Lists the files in the specified bucket asynchronously.
  /// </summary>
  /// <param name="bucket_name">The name of the bucket to list files from.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains a collection of file names (keys).</returns>
  Task<IEnumerable<string>> ListFilesAsync(string bucket_name);
}
