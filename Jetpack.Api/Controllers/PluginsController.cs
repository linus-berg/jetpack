using System.Data;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Jetpack.Api.Models;
using Jetpack.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jetpack.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PluginsController : ControllerBase {
  private readonly string metadata_bucket_;
  private readonly PluginMetadataService metadata_service_;
  private readonly string plugins_bucket_;
  private readonly IStorageService storage_service_;

  public PluginsController(IStorageService storage_service,
                           PluginMetadataService metadata_service,
                           IConfiguration configuration) {
    storage_service_ = storage_service;
    metadata_service_ = metadata_service;
    plugins_bucket_ = configuration["Minio:PluginsBucket"] ??
                      throw new InvalidOperationException();
    metadata_bucket_ = configuration["Minio:MetadataBucket"] ??
                       throw new InvalidOperationException();
  }

  [HttpPost("upload")]
  [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
  public async Task<IActionResult> UploadPlugin() {
    if (Request.ContentLength == 0) {
      return BadRequest("No file uploaded.");
    }

    using MemoryStream memory_stream = new();
    await Request.Body.CopyToAsync(memory_stream);
    memory_stream.Position = 0;

    await using ZipArchive archive = new(
      memory_stream,
      ZipArchiveMode.Read,
      true
    );

    // 1. Try to find META-INF/plugin.xml directly (simple zip structure)
    // I don't think this will ever happen, but just in case
    ZipArchiveEntry? plugin_xml_entry = archive.GetEntry("META-INF/plugin.xml");
    if (plugin_xml_entry != null) {
      await using Stream plugin_xml_stream = plugin_xml_entry.Open();
      return await ProcessPluginXml(plugin_xml_stream, memory_stream);
    }

    // 2. If not found, look for a jar file inside the zip that contains META-INF/plugin.xml
    // Plugins are often packaged as a zip containing a folder which contains lib/*.jar
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

        if (jar_plugin_xml_entry != null) {
          await using Stream jar_plugin_xml_stream =
            jar_plugin_xml_entry.Open();
          return await ProcessPluginXml(jar_plugin_xml_stream, memory_stream);
        }
      } catch (InvalidDataException) {
        // Not a valid zip/jar, skip it
      }
    }

    return BadRequest(
      "Invalid plugin: META-INF/plugin.xml not found in zip or nested jar."
    );
  }

  private async Task<IActionResult> ProcessPluginXml(
    Stream plugin_xml_stream, MemoryStream original_zip_stream) {
    XDocument doc;
    try {
      doc = XDocument.Load(plugin_xml_stream);
    } catch {
      return BadRequest("Invalid plugin.xml format.");
    }

    XElement? root = doc.Root;
    if (root == null) {
      return BadRequest("Invalid plugin.xml: No root element.");
    }

    PluginMetadata metadata = new() {
      id = (root.Element("id")?.Value ?? root.Element("name")?.Value) ??
           throw new InvalidOperationException("No id could be set for plugin"),
      name = root.Element("name")?.Value ??
             throw new InvalidOperationException("No name found for plugin"),
      version = root.Element("version")?.Value ?? throw new VersionNotFoundException(),
      description = root.Element("description")?.Value ?? "",
      change_notes = root.Element("change-notes")?.Value ?? "",
      vendor = root.Element("vendor")?.Value ?? "jetpack",
      since_build =
        root.Element("idea-version")?.Attribute("since-build")?.Value!,
      until_build = root.Element("idea-version")
                        ?.Attribute("until-build")
                        ?.Value
    };

    if (string.IsNullOrEmpty(metadata.id) ||
        string.IsNullOrEmpty(metadata.version)) {
      return BadRequest("Plugin ID or Version missing in plugin.xml");
    }

    // Upload Plugin Zip
    string plugin_file_name = $"{metadata.id}-{metadata.version}.zip";
    original_zip_stream.Position = 0;
    await storage_service_.UploadFileAsync(
      plugins_bucket_,
      plugin_file_name,
      original_zip_stream,
      "application/zip"
    );

    // Upload Metadata
    // We use a unique filename for each version to avoid overwriting
    string metadata_file_name = $"{metadata.id}-{metadata.version}.xml";
    string metadata_xml = GenerateUpdatePluginsXml(metadata, plugin_file_name);
    using MemoryStream metadata_stream =
      new(Encoding.UTF8.GetBytes(metadata_xml));
    await storage_service_.UploadFileAsync(
      metadata_bucket_,
      metadata_file_name,
      metadata_stream,
      "application/xml"
    );

    // Update in-memory cache
    await metadata_service_.AddMetadataAsync(metadata_xml);

    return Ok(
      new {
        Message = "Plugin uploaded successfully",
        Metadata = metadata
      }
    );
  }

  private string GenerateUpdatePluginsXml(PluginMetadata metadata,
                                          string plugin_file_name) {
    string download_url =
      $"{Request.Scheme}://{Request.Host}/api/plugins/download/{plugin_file_name}";

    XElement xml = new(
      "plugin",
      new XAttribute("id", metadata.id),
      new XAttribute("url", download_url),
      new XAttribute("version", metadata.version),
      new XElement("name", metadata.name),
      new XElement("description", metadata.description),
      new XElement("change-notes", metadata.change_notes),
      new XElement(
        "idea-version",
        new XAttribute("since-build", metadata.since_build ?? ""),
        new XAttribute("until-build", metadata.until_build ?? "")
      ),
      new XElement("vendor", metadata.vendor)
    );

    return xml.ToString();
  }

  [HttpGet("updatePlugins.xml")]
  public async Task<IActionResult> GetUpdatePluginsXml() {
    string xml = await metadata_service_.GetMetadataXmlAsync();
    return Content(xml, "application/xml");
  }

  [HttpGet("download/{file_name}")]
  public async Task<IActionResult> DownloadPlugin(string file_name) {
    if (!await storage_service_.FileExistsAsync(plugins_bucket_, file_name)) {
      return NotFound();
    }

    Stream stream =
      await storage_service_.GetFileAsync(plugins_bucket_, file_name);
    return File(stream, "application/zip", file_name);
  }
}