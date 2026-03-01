namespace Jetpack.Api.Services;

public interface IStorageService {
  Task UploadFileAsync(string bucket_name, string object_name, Stream data,
                       string content_type);

  Task<Stream> GetFileAsync(string bucket_name, string object_name);
  Task<bool> FileExistsAsync(string bucket_name, string object_name);
  Task EnsureBucketExistsAsync(string bucket_name);
  Task<IEnumerable<string>> ListFilesAsync(string bucket_name);
}