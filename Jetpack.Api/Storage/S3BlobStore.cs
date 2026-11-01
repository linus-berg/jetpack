using System.Runtime.CompilerServices;
using Jetpack.Api.Configuration;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Jetpack.Api.Storage;

/// <summary>S3 compatible object storage (MinIO, Ceph, AWS, ...).</summary>
public sealed class S3BlobStore : IBlobStore, IDisposable {
  private readonly IMinioClient client_;
  private readonly ILogger<S3BlobStore> logger_;
  private readonly S3StorageOptions options_;
  private readonly string prefix_;

  public S3BlobStore(S3StorageOptions options, ILogger<S3BlobStore> logger) {
    if (string.IsNullOrWhiteSpace(options.Endpoint)) {
      throw new InvalidOperationException(
        "Storage:S3:Endpoint (or MINIO_ENDPOINT) must be set for S3 storage."
      );
    }

    options_ = options;
    logger_ = logger;
    prefix_ = options.Prefix.Trim('/');
    IMinioClient builder = new MinioClient()
                           .WithEndpoint(options.Endpoint)
                           .WithCredentials(
                             options.AccessKey,
                             options.SecretKey
                           )
                           .WithSSL(options.Secure);
    if (!string.IsNullOrWhiteSpace(options.Region)) {
      builder = builder.WithRegion(options.Region);
    }

    client_ = builder.Build();
  }

  public string Description =>
    $"s3 ({(options_.Secure ? "https" : "http")}://{options_.Endpoint}/" +
    $"{options_.Bucket}/{prefix_})";

  public void Dispose() {
    client_.Dispose();
  }

  public async Task InitializeAsync(CancellationToken ct) {
    bool exists = await client_.BucketExistsAsync(
      new BucketExistsArgs().WithBucket(options_.Bucket),
      ct
    );
    if (!exists) {
      logger_.LogInformation("Creating bucket {Bucket}", options_.Bucket);
      MakeBucketArgs args = new MakeBucketArgs().WithBucket(options_.Bucket);
      if (!string.IsNullOrWhiteSpace(options_.Region)) {
        args = args.WithLocation(options_.Region);
      }

      await client_.MakeBucketAsync(args, ct);
    }
  }

  public async Task PutAsync(string key, Stream content, long length,
                             string content_type, CancellationToken ct) {
    await client_.PutObjectAsync(
      new PutObjectArgs()
        .WithBucket(options_.Bucket)
        .WithObject(ToObjectName(key))
        .WithStreamData(content)
        .WithObjectSize(length)
        .WithContentType(content_type),
      ct
    );
  }

  public async Task<BlobInfo?> GetInfoAsync(string key, CancellationToken ct) {
    try {
      ObjectStat stat = await client_.StatObjectAsync(
        new StatObjectArgs()
          .WithBucket(options_.Bucket)
          .WithObject(ToObjectName(key)),
        ct
      );
      return new BlobInfo(
        key,
        stat.Size,
        new DateTimeOffset(
          DateTime.SpecifyKind(stat.LastModified, DateTimeKind.Utc)
        )
      );
    } catch (ObjectNotFoundException) {
      return null;
    }
  }

  public async Task<bool> CopyToAsync(string key, Stream destination,
                                      CancellationToken ct) {
    try {
      await client_.GetObjectAsync(
        new GetObjectArgs()
          .WithBucket(options_.Bucket)
          .WithObject(ToObjectName(key))
          .WithCallbackStream(
            async (stream, token) => await stream.CopyToAsync(
                                       destination,
                                       1024 * 1024,
                                       token
                                     )
          ),
        ct
      );
      return true;
    } catch (ObjectNotFoundException) {
      return false;
    }
  }

  public async IAsyncEnumerable<BlobInfo> ListAsync(string prefix,
    [EnumeratorCancellation] CancellationToken ct) {
    ListObjectsArgs args = new ListObjectsArgs()
                           .WithBucket(options_.Bucket)
                           .WithPrefix(ToObjectName(prefix, false))
                           .WithRecursive(true);
    await foreach (Item item in client_.ListObjectsEnumAsync(args, ct)) {
      if (item.IsDir) {
        continue;
      }

      string? key = ToKey(item.Key);
      if (key == null) {
        continue;
      }

      DateTime modified = item.LastModifiedDateTime ?? DateTime.UnixEpoch;
      yield return new BlobInfo(
        key,
        (long)item.Size,
        new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc))
      );
    }
  }

  public async Task DeleteAsync(string key, CancellationToken ct) {
    await client_.RemoveObjectAsync(
      new RemoveObjectArgs()
        .WithBucket(options_.Bucket)
        .WithObject(ToObjectName(key)),
      ct
    );
  }

  private string ToObjectName(string key, bool validate = true) {
    if (validate) {
      BlobStoreExtensions.ValidateKey(key);
    }

    return prefix_.Length == 0 ? key : $"{prefix_}/{key}";
  }

  private string? ToKey(string object_name) {
    if (prefix_.Length == 0) {
      return object_name;
    }

    string with_slash = prefix_ + "/";
    return object_name.StartsWith(with_slash, StringComparison.Ordinal)
             ? object_name[with_slash.Length..]
             : null;
  }
}
