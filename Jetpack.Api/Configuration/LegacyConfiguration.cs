namespace Jetpack.Api.Configuration;

/// <summary>
///   Keeps deployments of the previous Jetpack version working by mapping its
///   environment variables (<c>MINIO_*</c>, <c>JETPACK_API_KEY</c>) and its
///   <c>Minio</c>/<c>ApiKey</c> settings onto the current configuration keys.
///   Explicit new-style settings always win.
/// </summary>
public static class LegacyConfiguration {
  private static readonly (string LegacyEnv, string LegacyKey, string Key)[]
    S_MAPPINGS_ = [
      ("MINIO_ENDPOINT", "Minio:Endpoint", "Storage:S3:Endpoint"),
      ("MINIO_ACCESS_KEY", "Minio:AccessKey", "Storage:S3:AccessKey"),
      ("MINIO_SECRET_KEY", "Minio:SecretKey", "Storage:S3:SecretKey"),
      ("MINIO_SECURE", "Minio:Secure", "Storage:S3:Secure"),
      ("MINIO_PLUGINS_BUCKET", "Minio:PluginsBucket", "Storage:S3:Bucket"),
      ("MINIO_OBJECT_PREFIX", "Minio:ObjectPrefix", "Storage:S3:Prefix"),
      ("JETPACK_API_KEY", "ApiKey", "Jetpack:ApiKey")
    ];

  public static void Apply(ConfigurationManager configuration) {
    Dictionary<string, string?> mapped = new();
    foreach ((string legacy_env, string legacy_key, string key) in
             S_MAPPINGS_) {
      string? value = Environment.GetEnvironmentVariable(legacy_env);
      if (string.IsNullOrEmpty(value)) {
        value = configuration[legacy_key];
      }

      if (string.IsNullOrEmpty(value) ||
          !string.IsNullOrEmpty(configuration[key])) {
        continue;
      }

      mapped[key] = value;
    }

    // The old version only knew MinIO, so legacy MinIO settings imply S3.
    bool explicit_provider =
      Environment.GetEnvironmentVariable("Storage__Provider") != null ||
      configuration.GetSection("Storage:Provider").Value != null;
    if (mapped.ContainsKey("Storage:S3:Endpoint") && !explicit_provider) {
      mapped["Storage:Provider"] = "S3";
    }

    if (mapped.Count > 0) {
      configuration.AddInMemoryCollection(mapped);
    }
  }
}
