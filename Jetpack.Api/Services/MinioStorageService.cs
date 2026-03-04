using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;

namespace Jetpack.Api.Services;

/// <summary>
///   Implementation of <see cref="IStorageService" /> using MinIO object storage.
/// </summary>
public class MinioStorageService : IStorageService {
  private readonly ILogger<MinioStorageService> logger_;
  private readonly IMinioClient minio_client_;
  private readonly string object_prefix_;

  /// <summary>
  ///   Initializes a new instance of the <see cref="MinioStorageService" /> class.
  ///   Configures the MinIO client using settings from the environment variables or configuration.
  /// </summary>
  /// <param name="configuration">The application configuration.</param>
  /// <param name="logger">The logger instance.</param>
  public MinioStorageService(IConfiguration configuration,
                             ILogger<MinioStorageService> logger) {
    logger_ = logger;
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
    object_prefix_ =
      Environment.GetEnvironmentVariable("MINIO_OBJECT_PREFIX") ??
      configuration["Minio:ObjectPrefix"] ?? "jetpack";

    logger_.LogInformation(
      "Initializing MinioStorageService with Endpoint: {Endpoint}, Secure: {Secure}, Prefix: {Prefix}",
      endpoint,
      secure,
      object_prefix_
    );

    // Initialize the MinIO client builder
    IMinioClient? builder = new MinioClient()
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
    try {
      await EnsureBucketExistsAsync(bucket_name);

      string final_object_name = GetPrefixedObjectName(object_name);
      logger_.LogDebug(
        "Uploading file to Bucket: {Bucket}, Object: {Object} (Prefixed: {PrefixedObject})",
        bucket_name,
        object_name,
        final_object_name
      );

      PutObjectArgs? put_object_args = new PutObjectArgs()
                                       .WithBucket(bucket_name)
                                       .WithObject(final_object_name)
                                       .WithStreamData(data)
                                       .WithObjectSize(data.Length)
                                       .WithContentType(content_type);
      await minio_client_.PutObjectAsync(put_object_args);
      logger_.LogInformation(
        "Successfully uploaded file: {Object} to bucket: {Bucket}",
        object_name,
        bucket_name
      );
    } catch (Exception ex) {
      logger_.LogError(
        ex,
        "Error uploading file: {Object} to bucket: {Bucket}",
        object_name,
        bucket_name
      );
      throw;
    }
  }

  /// <inheritdoc />
  public async Task<Stream>
    GetFileAsync(string bucket_name, string object_name) {
    try {
      MemoryStream memory_stream = new();
      string final_object_name = GetPrefixedObjectName(object_name);
      logger_.LogDebug(
        "Retrieving file from Bucket: {Bucket}, Object: {Object} (Prefixed: {PrefixedObject})",
        bucket_name,
        object_name,
        final_object_name
      );

      // Callback to copy the stream data
      GetObjectArgs? get_object_args = new GetObjectArgs()
                                       .WithBucket(bucket_name)
                                       .WithObject(final_object_name)
                                       .WithCallbackStream(
                                         stream => stream.CopyTo(memory_stream)
                                       );

      await minio_client_.GetObjectAsync(get_object_args);
      memory_stream.Position = 0;
      return memory_stream;
    } catch (Exception ex) {
      logger_.LogError(
        ex,
        "Error retrieving file: {Object} from bucket: {Bucket}",
        object_name,
        bucket_name
      );
      throw;
    }
  }

  /// <inheritdoc />
  public async Task<bool> FileExistsAsync(string bucket_name,
                                          string object_name) {
    try {
      string final_object_name = GetPrefixedObjectName(object_name);
      StatObjectArgs? stat_object_args = new StatObjectArgs()
                                         .WithBucket(bucket_name)
                                         .WithObject(final_object_name);
      await minio_client_.StatObjectAsync(stat_object_args);
      return true;
    } catch (Exception ex) {
      // Minio throws exception if object doesn't exist, so we catch it and return false
      // We only log at Debug level here as this is often an expected condition
      logger_.LogDebug(
        ex,
        "File check failed (likely does not exist): {Object} in bucket: {Bucket}",
        object_name,
        bucket_name
      );
      return false;
    }
  }

  /// <inheritdoc />
  public async Task EnsureBucketExistsAsync(string bucket_name) {
    try {
      BucketExistsArgs? bucket_exists_args =
        new BucketExistsArgs().WithBucket(bucket_name);
      bool found = await minio_client_.BucketExistsAsync(bucket_exists_args);
      if (!found) {
        logger_.LogInformation(
          "Bucket {Bucket} does not exist. Creating it...",
          bucket_name
        );
        MakeBucketArgs? make_bucket_args =
          new MakeBucketArgs().WithBucket(bucket_name);
        await minio_client_.MakeBucketAsync(make_bucket_args);
        logger_.LogInformation(
          "Bucket {Bucket} created successfully",
          bucket_name
        );
      }
    } catch (Exception ex) {
      logger_.LogError(
        ex,
        "Error ensuring bucket exists: {Bucket}",
        bucket_name
      );
      throw;
    }
  }

  /// <inheritdoc />
  public async Task<IEnumerable<string>> ListFilesAsync(string bucket_name) {
    try {
      await EnsureBucketExistsAsync(bucket_name);
      List<string> files = new();

      ListObjectsArgs? list_args = new ListObjectsArgs()
                                   .WithBucket(bucket_name)
                                   .WithRecursive(true);

      if (!string.IsNullOrEmpty(object_prefix_)) {
        list_args.WithPrefix($"{object_prefix_}/");
      }

      logger_.LogDebug(
        "Listing files in Bucket: {Bucket} with Prefix: {Prefix}",
        bucket_name,
        object_prefix_
      );

      IAsyncEnumerable<Item>? observable =
        minio_client_.ListObjectsEnumAsync(list_args);
      await foreach (Item item in observable) {
        // We need to return the logical names (without the internal prefix)
        files.Add(RemovePrefixFromObjectName(item.Key));
      }

      return files;
    } catch (Exception ex) {
      logger_.LogError(
        ex,
        "Error listing files in bucket: {Bucket}",
        bucket_name
      );
      throw;
    }
  }

  private string GetPrefixedObjectName(string object_name) {
    if (string.IsNullOrEmpty(object_prefix_)) {
      return object_name;
    }

    // Ensure we don't double-prefix if logic changes later, though simple concatenation is usually enough
    return $"{object_prefix_}/{object_name}";
  }

  private string RemovePrefixFromObjectName(string object_name) {
    if (string.IsNullOrEmpty(object_prefix_)) {
      return object_name;
    }

    string prefix_with_slash = $"{object_prefix_}/";
    if (object_name.StartsWith(prefix_with_slash)) {
      return object_name.Substring(prefix_with_slash.Length);
    }

    return object_name;
  }
}