using System.Data;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Jetpack.Api.Models;
using Jetpack.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jetpack.Api.Controllers;

/// <summary>
///   Controller responsible for handling plugin uploads, downloads, and metadata retrieval.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PluginsController : ControllerBase {
  private readonly bool https_;
  private readonly ILogger<PluginsController> logger_;
  private readonly string metadata_bucket_;
  private readonly PluginMetadataService metadata_service_;
  private readonly string plugins_bucket_;
  private readonly IStorageService storage_service_;

  /// <summary>
  ///   Initializes a new instance of the <see cref="PluginsController" /> class.
  /// </summary>
  /// <param name="storage_service">The storage service for file operations.</param>
  /// <param name="metadata_service">The service for managing plugin metadata.</param>
  /// <param name="configuration">The application configuration.</param>
  /// <param name="logger">The logger instance.</param>
  /// <exception cref="InvalidOperationException">Thrown if bucket configurations are missing.</exception>
  public PluginsController(IStorageService storage_service,
                           PluginMetadataService metadata_service,
                           IConfiguration configuration,
                           ILogger<PluginsController> logger) {
    storage_service_ = storage_service;
    metadata_service_ = metadata_service;
    logger_ = logger;
    https_ = bool.Parse(
      Environment.GetEnvironmentVariable("JETPACK_USE_HTTPS") ??
      configuration["Https"] ?? "false"
    );
    plugins_bucket_ =
      Environment.GetEnvironmentVariable("MINIO_PLUGINS_BUCKET") ??
      configuration["Minio:PluginsBucket"] ??
      throw new InvalidOperationException(
        "Plugins bucket configuration is missing."
      );
    metadata_bucket_ =
      Environment.GetEnvironmentVariable("MINIO_METADATA_BUCKET") ??
      configuration["Minio:MetadataBucket"] ??
      throw new InvalidOperationException(
        "Metadata bucket configuration is missing."
      );
  }

  /// <summary>
  ///   Uploads a plugin file (ZIP archive).
  ///   The method extracts metadata from the plugin.xml file within the archive and stores both the plugin file and its
  ///   metadata.
  /// </summary>
  /// <returns>An <see cref="IActionResult" /> indicating the result of the upload operation.</returns>
  [HttpPost("upload")]
  [ApiKeyAuthorize]
  [RequestSizeLimit(2000 * 1024 * 1024)] // 500 MB
  public async Task<IActionResult> UploadPlugin() {
    if (Request.ContentLength == 0) {
      logger_.LogWarning("UploadPlugin called with empty body.");
      return BadRequest("No file uploaded.");
    }

    logger_.LogInformation(
      "Starting plugin upload. Size: {Size} bytes",
      Request.ContentLength
    );

    using MemoryStream memory_stream = new();
    await Request.Body.CopyToAsync(memory_stream);
    memory_stream.Position = 0;

    await using ZipArchive archive = new(
      memory_stream,
      ZipArchiveMode.Read,
      true
    );

    // 1. Try to find META-INF/plugin.xml directly (simple zip structure)
    ZipArchiveEntry? plugin_xml_entry = archive.GetEntry("META-INF/plugin.xml");
    if (plugin_xml_entry != null) {
      logger_.LogDebug("Found META-INF/plugin.xml directly in zip.");
      await using Stream plugin_xml_stream = plugin_xml_entry.Open();
      return await ProcessPluginXml(plugin_xml_stream, memory_stream);
    }

    // 2. If not found, look for a jar file inside the zip that contains META-INF/plugin.xml
    foreach (ZipArchiveEntry entry in archive.Entries) {
      if (!entry.FullName.EndsWith(
            ".jar",
            StringComparison.OrdinalIgnoreCase
          )) {
        continue;
      }

      // We need to read the jar file content
      await using Stream jar_stream = entry.Open();
      using MemoryStream jar_memory_stream = new();
      await jar_stream.CopyToAsync(jar_memory_stream);
      jar_memory_stream.Position = 0;

      try {
        await using ZipArchive jar_archive = new(
          jar_memory_stream,
          ZipArchiveMode.Read
        );
        ZipArchiveEntry? jar_plugin_xml_entry =
          jar_archive.GetEntry("META-INF/plugin.xml");

        if (jar_plugin_xml_entry == null) {
          continue;
        }

        logger_.LogDebug(
          "Found META-INF/plugin.xml inside nested jar: {JarName}",
          entry.FullName
        );
        await using Stream jar_plugin_xml_stream =
          jar_plugin_xml_entry.Open();
        return await ProcessPluginXml(jar_plugin_xml_stream, memory_stream);
      } catch (InvalidDataException) {
        // Not a valid zip/jar, skip it
        logger_.LogDebug(
          "Skipping invalid jar file: {JarName}",
          entry.FullName
        );
      }
    }

    logger_.LogWarning("Upload failed: META-INF/plugin.xml not found.");
    return BadRequest(
      "Invalid plugin: META-INF/plugin.xml not found in zip or nested jar."
    );
  }

  /// <summary>
  ///   Processes the plugin.xml stream to extract metadata and upload the plugin.
  /// </summary>
  /// <param name="plugin_xml_stream">The stream containing the plugin.xml content.</param>
  /// <param name="original_zip_stream">The stream containing the original uploaded zip file.</param>
  /// <returns>An <see cref="IActionResult" /> indicating the result of the processing.</returns>
  private async Task<IActionResult> ProcessPluginXml(
    Stream plugin_xml_stream, MemoryStream original_zip_stream) {
    XDocument doc;
    try {
      doc = XDocument.Load(plugin_xml_stream);
    } catch (Exception ex) {
      logger_.LogError(ex, "Failed to parse plugin.xml.");
      return BadRequest("Invalid plugin.xml format.");
    }

    XElement? root = doc.Root;
    if (root == null) {
      logger_.LogWarning("plugin.xml has no root element.");
      return BadRequest("Invalid plugin.xml: No root element.");
    }

    PluginMetadata metadata;
    try {
      metadata = new PluginMetadata {
        id = (root.Element("id")?.Value ?? root.Element("name")?.Value) ??
             throw new InvalidOperationException(
               "No id could be set for plugin"
             ),
        name = root.Element("name")?.Value ??
               throw new InvalidOperationException("No name found for plugin"),
        version = root.Element("version")?.Value ??
                  throw new VersionNotFoundException(),
        description = root.Element("description")?.Value ?? "",
        change_notes = root.Element("change-notes")?.Value ?? "",
        vendor = root.Element("vendor")?.Value ?? "jetpack",
        since_build =
          root.Element("idea-version")?.Attribute("since-build")?.Value!,
        until_build = root.Element("idea-version")
                          ?.Attribute("until-build")
                          ?.Value
      };
    } catch (Exception ex) {
      logger_.LogError(ex, "Error extracting metadata from plugin.xml");
      return BadRequest($"Invalid plugin.xml metadata: {ex.Message}");
    }

    if (string.IsNullOrEmpty(metadata.id) ||
        string.IsNullOrEmpty(metadata.version)) {
      logger_.LogWarning("Plugin ID or Version missing in plugin.xml");
      return BadRequest("Plugin ID or Version missing in plugin.xml");
    }

    logger_.LogInformation(
      "Processing plugin: {Id} v{Version}",
      metadata.id,
      metadata.version
    );

    // Upload Plugin Zip
    string plugin_file_name = $"{metadata.id}-{metadata.version}.zip";
    original_zip_stream.Position = 0;
    try {
      await storage_service_.UploadFileAsync(
        plugins_bucket_,
        plugin_file_name,
        original_zip_stream,
        "application/zip"
      );
    } catch (Exception ex) {
      logger_.LogError(ex, "Failed to upload plugin zip file to storage.");
      return StatusCode(500, "Failed to upload plugin file.");
    }

    // Upload Metadata
    // We use a unique filename for each version to avoid overwriting
    string metadata_file_name = $"{metadata.id}-{metadata.version}.xml";
    string metadata_xml = GenerateUpdatePluginsXml(metadata, plugin_file_name);
    using MemoryStream metadata_stream =
      new(Encoding.UTF8.GetBytes(metadata_xml));

    try {
      await storage_service_.UploadFileAsync(
        metadata_bucket_,
        metadata_file_name,
        metadata_stream,
        "application/xml"
      );
    } catch (Exception ex) {
      logger_.LogError(ex, "Failed to upload metadata xml to storage.");
      // We might want to rollback the zip upload here in a real production scenario
      return StatusCode(500, "Failed to upload metadata file.");
    }

    // Update in-memory cache
    await metadata_service_.AddMetadataAsync(metadata_xml);

    logger_.LogInformation(
      "Plugin {Id} v{Version} uploaded successfully.",
      metadata.id,
      metadata.version
    );

    return Ok(
      new {
        Message = "Plugin uploaded successfully",
        Metadata = metadata
      }
    );
  }

  /// <summary>
  ///   Generates the XML string for the updatePlugins.xml file based on the plugin metadata.
  /// </summary>
  /// <param name="metadata">The plugin metadata.</param>
  /// <param name="plugin_file_name">The name of the plugin file.</param>
  /// <returns>The generated XML string.</returns>
  private string GenerateUpdatePluginsXml(PluginMetadata metadata,
                                          string plugin_file_name) {
    string scheme = https_ ? "https" : "http";
    string download_url =
      $"{scheme}://{Request.Host}/api/plugins/download/{plugin_file_name}";
    XElement idea_version = new("idea-version");
    if (!string.IsNullOrEmpty(metadata.since_build)) {
      idea_version.SetAttributeValue("since-build", metadata.since_build);
    }

    if (!string.IsNullOrEmpty(metadata.until_build)) {
      idea_version.SetAttributeValue("until-build", metadata.until_build);
    }

    XElement xml = new(
      "plugin",
      new XAttribute("id", metadata.id),
      new XAttribute("url", download_url),
      new XAttribute("version", metadata.version),
      new XElement("name", metadata.name),
      new XElement("description", metadata.description),
      new XElement("change-notes", metadata.change_notes),
      idea_version,
      new XElement("vendor", metadata.vendor)
    );

    return xml.ToString();
  }

  /// <summary>
  ///   Retrieves the aggregated updatePlugins.xml containing metadata for all available plugins.
  /// </summary>
  /// <returns>The updatePlugins.xml content.</returns>
  [HttpGet("updatePlugins.xml")]
  public async Task<IActionResult> GetUpdatePluginsXml() {
    logger_.LogDebug("Serving updatePlugins.xml");
    string xml = await metadata_service_.GetMetadataXmlAsync();
    return Content(xml, "application/xml");
  }

  /// <summary>
  ///   Downloads a specific plugin file.
  /// </summary>
  /// <param name="file_name">The name of the file to download.</param>
  /// <returns>The file stream if found; otherwise, NotFound.</returns>
  [HttpGet("download/{file_name}")]
  public async Task<IActionResult> DownloadPlugin(string file_name) {
    logger_.LogDebug("Download request for: {FileName}", file_name);
    if (!await storage_service_.FileExistsAsync(plugins_bucket_, file_name)) {
      logger_.LogWarning("File not found: {FileName}", file_name);
      return NotFound();
    }

    Stream stream =
      await storage_service_.GetFileAsync(plugins_bucket_, file_name);
    return File(stream, "application/zip", file_name);
  }
}