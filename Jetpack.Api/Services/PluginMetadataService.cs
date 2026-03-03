using System.Xml.Linq;

namespace Jetpack.Api.Services;

/// <summary>
/// Service responsible for managing plugin metadata, including initialization, adding new metadata, and retrieving the aggregated XML.
/// </summary>
public class PluginMetadataService {
  private readonly ILogger<PluginMetadataService> logger_;
  private readonly SemaphoreSlim lock_ = new(1, 1);
  private readonly string metadata_bucket_;
  private readonly IStorageService storage_service_;
  private XDocument cached_metadata_;

  /// <summary>
  /// Initializes a new instance of the <see cref="PluginMetadataService"/> class.
  /// </summary>
  /// <param name="storage_service">The storage service used to persist and retrieve metadata files.</param>
  /// <param name="configuration">The application configuration.</param>
  /// <param name="logger">The logger instance.</param>
  /// <exception cref="InvalidOperationException">Thrown if the metadata bucket name is not configured.</exception>
  public PluginMetadataService(IStorageService storage_service,
                               IConfiguration configuration,
                               ILogger<PluginMetadataService> logger) {
    storage_service_ = storage_service;
    logger_ = logger;
    string? bucket = Environment.GetEnvironmentVariable("MINIO_METADATA_BUCKET") ??
                     configuration["Minio:MetadataBucket"];
    if (string.IsNullOrEmpty(bucket)) {
      throw new InvalidOperationException("Metadata bucket configuration is missing.");
    }
    metadata_bucket_ = bucket;
    cached_metadata_ = new XDocument(
      new XDeclaration("1.0", "UTF-8", null),
      new XElement("plugins")
    );
  }

  /// <summary>
  /// Initializes the metadata cache by loading existing metadata files from the storage bucket.
  /// </summary>
  /// <returns>A task that represents the asynchronous initialization operation.</returns>
  public async Task InitializeAsync() {
    logger_.LogInformation("Initializing PluginMetadataService...");
    await storage_service_.EnsureBucketExistsAsync(metadata_bucket_);
    IEnumerable<string> files =
      await storage_service_.ListFilesAsync(metadata_bucket_);
    XElement plugins_element = new("plugins");

    int loaded_count = 0;
    foreach (string file in files) {
      if (file.EndsWith(".xml")) {
        try {
          await using Stream stream =
            await storage_service_.GetFileAsync(metadata_bucket_, file);
          XDocument doc = XDocument.Load(stream);
          if (doc.Root != null) {
            plugins_element.Add(doc.Root);
            loaded_count++;
          }
        } catch (Exception ex) {
          logger_.LogWarning(ex, "Failed to load metadata file: {File}", file);
        }
      }
    }

    await lock_.WaitAsync();
    try {
      cached_metadata_ = new XDocument(
        new XDeclaration("1.0", "UTF-8", null),
        plugins_element
      );
      logger_.LogInformation(
        "Metadata initialization complete. Loaded {Count} plugins.",
        loaded_count
      );
    } finally {
      lock_.Release();
    }
  }

  /// <summary>
  /// Adds or updates plugin metadata from the provided XML content.
  /// </summary>
  /// <param name="xml_content">The XML string containing the plugin metadata.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  public async Task AddMetadataAsync(string xml_content) {
    XDocument new_doc = XDocument.Parse(xml_content);
    XElement? new_plugin = new_doc.Root;
    if (new_plugin == null) {
      return;
    }

    string? id = new_plugin.Attribute("id")?.Value;
    string? version = new_plugin.Attribute("version")?.Value;

    logger_.LogInformation(
      "Adding/Updating metadata for Plugin ID: {Id}, Version: {Version}",
      id,
      version
    );

    await lock_.WaitAsync();
    try {
      // Remove existing entry for same ID and Version if it exists
      XElement? existing = cached_metadata_.Root?.Elements("plugin")
                                           .FirstOrDefault(
                                             e =>
                                               e.Attribute("id")?.Value == id &&
                                               e.Attribute("version")?.Value ==
                                               version
                                           );

      if (existing != null) {
        logger_.LogDebug(
          "Removing existing metadata for Plugin ID: {Id}, Version: {Version}",
          id,
          version
        );
        existing.Remove();
      }

      cached_metadata_.Root?.Add(new_plugin);
    } finally {
      lock_.Release();
    }
  }

  /// <summary>
  /// Retrieves the aggregated plugin metadata as an XML string.
  /// </summary>
  /// <returns>A task that represents the asynchronous operation. The task result contains the metadata XML string.</returns>
  public async Task<string> GetMetadataXmlAsync() {
    await lock_.WaitAsync();
    try {
      return cached_metadata_.Declaration +
             Environment.NewLine +
             cached_metadata_;
    } finally {
      lock_.Release();
    }
  }
}
