using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;

namespace Jetpack.Api.Services;

/// <summary>
/// Implementation of <see cref="IStorageService"/> using MinIO object storage.
/// </summary>
public class MinioStorageService : IStorageService {
  private readonly IMinioClient minio_client_;

  /// <summary>
  /// Initializes a new instance of the <see cref="MinioStorageService"/> class.
  /// Configures the MinIO client using settings from the environment variables or configuration.
  /// </summary>
  /// <param name="configuration">The application configuration.</param>
  public MinioStorageService(IConfiguration configuration) {
    string? endpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT") ??
                       configuration["Minio:Endpoint"];
    string? access_key =
      Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ??
      configuration["Minio:AccessKey"];
    string? secret_key =
      Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ??
      configuration["Minio:SecretKey"];
    bool secure = bool.Parse(
      Environment.GetEnvironmentVariable("MINIO_SECURE") ??
      configuration["Minio:Secure"] ?? "false"
    );

    // Initialize the MinIO client builder
    var builder = new MinioClient()
                            .WithEndpoint(endpoint)
                            .WithCredentials(access_key, secret_key);

    if (secure) {
      builder.WithSSL();
    }

    minio_client_ = builder.Build();
  }

  /// <inheritdoc />
  public async Task UploadFileAsync(string bucket_name, string object_name,
                                    Stream data, string content_type) {
    await EnsureBucketExistsAsync(bucket_name);

    PutObjectArgs? put_object_args = new PutObjectArgs()
                                     .WithBucket(bucket_name)
                                     .WithObject(object_name)
                                     .WithStreamData(data)
                                     .WithObjectSize(data.Length)
                                     .WithContentType(content_type);

    await minio_client_.PutObjectAsync(put_object_args);
  }

  /// <inheritdoc />
  public async Task<Stream>
    GetFileAsync(string bucket_name, string object_name) {
    MemoryStream memory_stream = new();
    // Callback to copy the stream data
    GetObjectArgs? get_object_args = new GetObjectArgs()
                                     .WithBucket(bucket_name)
                                     .WithObject(object_name)
                                     .WithCallbackStream(
                                       stream => stream.CopyTo(memory_stream)
                                     );

    await minio_client_.GetObjectAsync(get_object_args);
    memory_stream.Position = 0;
    return memory_stream;
  }

  /// <inheritdoc />
  public async Task<bool> FileExistsAsync(string bucket_name,
                                          string object_name) {
    try {
      StatObjectArgs? stat_object_args = new StatObjectArgs()
                                         .WithBucket(bucket_name)
                                         .WithObject(object_name);
      await minio_client_.StatObjectAsync(stat_object_args);
      return true;
    } catch {
      return false;
    }
  }

  /// <inheritdoc />
  public async Task EnsureBucketExistsAsync(string bucket_name) {
    BucketExistsArgs? bucket_exists_args =
      new BucketExistsArgs().WithBucket(bucket_name);
    bool found = await minio_client_.BucketExistsAsync(bucket_exists_args);
    if (!found) {
      MakeBucketArgs? make_bucket_args =
        new MakeBucketArgs().WithBucket(bucket_name);
      await minio_client_.MakeBucketAsync(make_bucket_args);
    }
  }

  /// <inheritdoc />
  public async Task<IEnumerable<string>> ListFilesAsync(string bucket_name) {
    await EnsureBucketExistsAsync(bucket_name);
    List<string> files = new();
    ListObjectsArgs? list_args = new ListObjectsArgs().WithBucket(bucket_name)
      .WithRecursive(true);

    IAsyncEnumerable<Item>? observable =
      minio_client_.ListObjectsEnumAsync(list_args);
    await foreach (Item item in observable) {
      files.Add(item.Key);
    }

    return files;
  }
}
