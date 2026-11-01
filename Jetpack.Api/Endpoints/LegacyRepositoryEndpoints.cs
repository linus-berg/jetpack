using System.Text;
using System.Xml;
using Jetpack.Api.Compatibility;
using Jetpack.Api.Configuration;
using Jetpack.Api.Plugins;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Endpoints;

/// <summary>
///   The classic "custom plugin repository" format (Settings | Plugins |
///   Manage Plugin Repositories). Kept for IDEs configured against the previous
///   Jetpack version and for anyone who prefers that setup.
/// </summary>
public static class LegacyRepositoryEndpoints {
  public static void MapLegacyRepositoryEndpoints(
    this IEndpointRouteBuilder app) {
    app.MapGet("/api/plugins/updatePlugins.xml", UpdatePluginsXml);
    app.MapGet("/updatePlugins.xml", UpdatePluginsXml);
    app.MapGet("/plugins/list", UpdatePluginsXml);
    app.MapGet("/plugins/list/", UpdatePluginsXml);
    // Download URL format of the previous version: {id}-{version}.zip
    app.MapGet("/api/plugins/download/{fileName}", LegacyDownload);
  }

  private static async Task UpdatePluginsXml(HttpContext context,
                                             PluginCatalog catalog,
                                             IOptions<JetpackOptions> options) {
    CatalogSnapshot snapshot = catalog.Snapshot;
    BuildNumber? build = context.Request.Build();
    string etag = $"{snapshot.ETag[..^1]}-{build}\"";
    if (context.IsNotModified(etag)) {
      context.Response.StatusCode = StatusCodes.Status304NotModified;
      return;
    }

    string base_url = (options.Value.PublicUrl ?? "").TrimEnd('/');
    context.Response.ContentType = "application/xml; charset=utf-8";
    XmlWriterSettings settings = new() {
      Async = true,
      Indent = true,
      Encoding = new UTF8Encoding(false)
    };
    await using XmlWriter writer = XmlWriter.Create(
      context.Response.Body,
      settings
    );
    await writer.WriteStartDocumentAsync();
    await writer.WriteStartElementAsync(null, "plugins", null);
    foreach (PluginEntry plugin in snapshot.Plugins) {
      // Without a build every version is listed and the IDE picks the newest
      // compatible one itself.
      IEnumerable<ReleaseEntry> releases = build == null
                                             ? plugin.Releases
                                             : plugin.LatestCompatible(build) is
                                               { } compatible
                                               ? [compatible]
                                               : [];
      foreach (ReleaseEntry entry in releases) {
        await WritePluginAsync(writer, entry, base_url);
      }
    }

    await writer.WriteEndElementAsync();
    await writer.WriteEndDocumentAsync();
    await writer.FlushAsync();
  }

  private static async Task WritePluginAsync(XmlWriter writer,
                                             ReleaseEntry entry,
                                             string base_url) {
    PluginRelease release = entry.Release;
    await writer.WriteStartElementAsync(null, "plugin", null);
    await writer.WriteAttributeStringAsync(null, "id", null, release.PluginId);
    await writer.WriteAttributeStringAsync(
      null,
      "url",
      null,
      base_url + HttpHelpers.FileUrl(entry)
    );
    await writer.WriteAttributeStringAsync(
      null,
      "version",
      null,
      release.Version
    );
    await writer.WriteElementStringAsync(null, "name", null, release.Name);
    await writer.WriteStartElementAsync(null, "idea-version", null);
    if (release.SinceBuild != null) {
      await writer.WriteAttributeStringAsync(
        null,
        "since-build",
        null,
        release.SinceBuild
      );
    }

    if (release.UntilBuild != null) {
      await writer.WriteAttributeStringAsync(
        null,
        "until-build",
        null,
        release.UntilBuild
      );
    }

    await writer.WriteEndElementAsync();
    if (release.Vendor != null) {
      await writer.WriteStartElementAsync(null, "vendor", null);
      if (release.VendorUrl != null) {
        await writer.WriteAttributeStringAsync(
          null,
          "url",
          null,
          release.VendorUrl
        );
      }

      if (release.VendorEmail != null) {
        await writer.WriteAttributeStringAsync(
          null,
          "email",
          null,
          release.VendorEmail
        );
      }

      await writer.WriteStringAsync(release.Vendor);
      await writer.WriteEndElementAsync();
    }

    await WriteCDataAsync(writer, "description", release.Description);
    await WriteCDataAsync(writer, "change-notes", release.ChangeNotes);
    // The IDE treats every <depends> here as required.
    foreach (string dependency in release.Dependencies) {
      await writer.WriteElementStringAsync(null, "depends", null, dependency);
    }

    foreach (string tag in release.Tags) {
      await writer.WriteElementStringAsync(null, "tags", null, tag);
    }

    if (release.ProductCode != null) {
      await writer.WriteElementStringAsync(
        null,
        "product-code",
        null,
        release.ProductCode
      );
    }

    await writer.WriteEndElementAsync();
  }

  private static async Task WriteCDataAsync(XmlWriter writer, string name,
                                            string? value) {
    if (string.IsNullOrEmpty(value)) {
      return;
    }

    await writer.WriteStartElementAsync(null, name, null);
    // XmlWriter splits any "]]>" in the text into separate CDATA sections.
    await writer.WriteCDataAsync(value);
    await writer.WriteEndElementAsync();
  }

  private static IResult LegacyDownload(string fileName,
                                        PluginCatalog catalog) {
    ReleaseEntry? release = catalog.Snapshot.FindLegacyFile(fileName);
    return release == null
             ? Results.NotFound()
             : Results.Redirect(HttpHelpers.FileUrl(release));
  }
}
