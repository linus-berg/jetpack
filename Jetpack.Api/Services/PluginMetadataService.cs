using System.Xml.Linq;

namespace Jetpack.Api.Services;

public class PluginMetadataService {
  private readonly SemaphoreSlim lock_ = new(1, 1);
  private readonly string metadata_bucket_;
  private readonly IStorageService storage_service_;
  private XDocument cached_metadata_;

  public PluginMetadataService(IStorageService storage_service,
                               IConfiguration configuration) {
    storage_service_ = storage_service;
    metadata_bucket_ =
      (Environment.GetEnvironmentVariable("MINIO_METADATA_BUCKET") ??
       configuration["Minio:MetadataBucket"]) ??
      throw new InvalidOperationException();
    cached_metadata_ = new XDocument(
      new XDeclaration("1.0", "UTF-8", null),
      new XElement("plugins")
    );
  }

  public async Task InitializeAsync() {
    await storage_service_.EnsureBucketExistsAsync(metadata_bucket_);
    IEnumerable<string> files =
      await storage_service_.ListFilesAsync(metadata_bucket_);
    XElement plugins_element = new("plugins");

    foreach (string file in files) {
      if (file.EndsWith(".xml")) {
        try {
          await using Stream stream =
            await storage_service_.GetFileAsync(metadata_bucket_, file);
          XDocument doc = XDocument.Load(stream);
          if (doc.Root != null) {
            plugins_element.Add(doc.Root);
          }
        } catch {
          // Ignore malformed files
        }
      }
    }

    await lock_.WaitAsync();
    try {
      cached_metadata_ = new XDocument(
        new XDeclaration("1.0", "UTF-8", null),
        plugins_element
      );
    } finally {
      lock_.Release();
    }
  }

  public async Task AddMetadataAsync(string xml_content) {
    XDocument new_doc = XDocument.Parse(xml_content);
    XElement? new_plugin = new_doc.Root;
    if (new_plugin == null) {
      return;
    }

    string? id = new_plugin.Attribute("id")?.Value;
    string? version = new_plugin.Attribute("version")?.Value;

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

      existing?.Remove();

      cached_metadata_.Root?.Add(new_plugin);
    } finally {
      lock_.Release();
    }
  }

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