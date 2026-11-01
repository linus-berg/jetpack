using System.Text.Json;
using Jetpack.Api.Compatibility;
using Jetpack.Api.Configuration;
using Jetpack.Api.Plugins;
using Jetpack.Api.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Jetpack.Api.Endpoints;

/// <summary>
///   Emulates the parts of plugins.jetbrains.com that IntelliJ-based IDEs use
///   (see <c>MarketplaceUrls.kt</c> in intellij-community). Point
///   <c>plugins.jetbrains.com</c> at this server (DNS + trusted TLS
///   certificate), or start the IDE with
///   <c>-Didea.plugins.host=https://your-server</c>, and the stock
///   Marketplace tab, plugin updates and dependency resolution work offline.
/// </summary>
public static class MarketplaceEndpoints {
  private const string C_JSON_ = "application/json";

  public static void MapMarketplaceEndpoints(this IEndpointRouteBuilder app) {
    // Marketplace tab: listing, search, filters.
    app.MapGet("/api/search/plugins", SearchPlugins);
    app.MapGet("/api/search/aggregation/{field}", Aggregation);

    // Update checks and dependency resolution. IDEs <= 2025.3 POST to
    // compatibleUpdates, newer ones (2024.2+) GET updates/compatible.
    app.MapGet("/api/search/updates/compatible", CompatibleUpdatesGet);
    app.MapPost("/api/search/compatibleUpdates", CompatibleUpdatesPost);
    app.MapPost("/api/search/updates/nearest", NearestUpdates);

    // Metadata files.
    app.MapGet("/files/{pluginId:int}/{updateId:int}/meta.json", UpdateMeta);
    app.MapGet("/files/{pluginId:int}/meta.json", PluginMeta);
    app.MapGet("/files/pluginsXMLIds.json", PluginXmlIds);
    app.MapGet("/files/jbPluginsXMLIds.json", JetBrainsPluginXmlIds);
    app.MapGet("/files/brokenPlugins.json", BrokenPlugins);
    app.MapGet(
      "/files/IDE/extensions.json",
      () => Results.Content("{}", C_JSON_)
    );
    app.MapGet("/api/icon", Icon);

    // Downloads: /pluginManager?id=..&build=.. redirects to the artifact.
    app.MapGet("/pluginManager", PluginManagerDownload);
    app.MapGet("/pluginManager/{**rest}", PluginManagerDownload);
    app.MapGet("/plugin/download", WebsiteDownload);
    app.MapMethods(
      "/files/{pluginId:int}/{updateId:int}/{fileName}",
      [HttpMethods.Get, HttpMethods.Head],
      DownloadFile
    );

    // Features the IDE queries that have no offline equivalent.
    app.MapGet(
      "/feature/getImplementations",
      () => Results.Content("[]", C_JSON_)
    );
    app.MapGet(
      "/api/products/intellij/plugins/{xmlId}/comments",
      () => Results.Content("[]", C_JSON_)
    );
  }

  private static IResult SearchPlugins(HttpRequest request,
                                       PluginCatalog catalog,
                                       DownloadStatistics statistics,
                                       IOptions<JetpackOptions> options) {
    SearchQuery query = new() {
      Text = request.QueryString("search"),
      OrderBy = request.QueryString("orderBy"),
      Featured = request.QueryFlag("is_featured_search"),
      Tags = request.QueryValues("tags"),
      Organizations = request.QueryValues("organization", "organizations"),
      Build = request.Build(),
      IncludeIncompatible = request.QueryFlag("all"),
      Offset = request.QueryInt("offset", 0),
      Max = request.QueryInt("max", 20)
    };
    IReadOnlyList<SearchHit> hits = PluginSearch.Search(
      catalog.Snapshot,
      query,
      statistics.Get,
      options.Value.FeaturedPlugins
    );
    return Results.Json(
      hits.Select(h => ToSearchItem(h, statistics.Get(h.Plugin.Id))).ToList()
    );
  }

  private static SearchPluginItem ToSearchItem(SearchHit hit, long downloads) {
    PluginEntry plugin = hit.Plugin;
    ReleaseEntry shown = hit.Compatible ?? plugin.Latest;
    string? preview = PluginEntry.ToPlainText(shown.Release.Description);
    if (preview.Length > 200) {
      preview = preview[..197] + "...";
    }

    return new SearchPluginItem(
      plugin.NumericId,
      plugin.Id,
      plugin.Name,
      preview.Length > 0 ? preview : null,
      false,
      downloads,
      plugin.Vendor,
      HttpHelpers.ToUnixMilliseconds(shown.Release.UploadedAt),
      hit.Compatible?.UpdateId,
      plugin.Latest.Release.HasIcon
        ? $"/api/icon?pluginId={Uri.EscapeDataString(plugin.Id)}"
        : null,
      plugin.Tags,
      plugin.Vendor == null ? null : new VendorInfo(plugin.Vendor, false),
      hit.Compatible == null ? ToNearestUpdate(plugin.Latest, false) : null
    );
  }

  private static IResult Aggregation(string field, HttpRequest request,
                                     PluginCatalog catalog) {
    BuildNumber? build = request.Build();
    List<PluginEntry> plugins = catalog.Snapshot
                                       .Plugins
                                       .Where(
                                         p => p.LatestCompatible(build) != null
                                       )
                                       .ToList();
    IEnumerable<string> values = field.ToLowerInvariant() switch {
      "tags" => plugins.SelectMany(p => p.Tags),
      "organizations" or "organization" or "vendors" => plugins
                                                       .Select(p => p.Vendor)
                                                       .OfType<string>(),
      _ => []
    };
    Dictionary<string, int> aggregations = values
                                           .GroupBy(
                                             v => v,
                                             StringComparer.OrdinalIgnoreCase
                                           )
                                           .OrderBy(
                                             g => g.Key,
                                             StringComparer.OrdinalIgnoreCase
                                           )
                                           .ToDictionary(
                                             g => g.First(),
                                             g => g.Count()
                                           );
    return Results.Json(new AggregationResponse(aggregations, plugins.Count));
  }

  private static IResult CompatibleUpdatesGet(HttpRequest request,
                                              PluginCatalog catalog) {
    return Results.Json(
      FindCompatibleUpdates(
        catalog.Snapshot,
        request.QueryValues("pluginXmlId", "pluginXMLId", "pluginXMLIds"),
        request.QueryString("module"),
        request.Build()
      )
    );
  }

  private static async Task<IResult> CompatibleUpdatesPost(
    HttpRequest request, PluginCatalog catalog) {
    CompatibleUpdateRequest? body = await ReadBodyAsync(request);
    if (body == null) {
      return Results.BadRequest("Expected a JSON body.");
    }

    return Results.Json(
      FindCompatibleUpdates(
        catalog.Snapshot,
        body.PluginXmlIds ?? [],
        body.Module,
        BuildNumber.TryParse(body.Build) ?? request.Build()
      )
    );
  }

  private static List<IdeCompatibleUpdate> FindCompatibleUpdates(
    CatalogSnapshot snapshot, IReadOnlyList<string> plugin_ids,
    string? module, BuildNumber? build) {
    List<IdeCompatibleUpdate> updates = new();
    IEnumerable<PluginEntry> plugins = module != null
                                         ? snapshot.FindByModule(module)
                                         : plugin_ids
                                           .Distinct(StringComparer.Ordinal)
                                           .Select(snapshot.FindPlugin)
                                           .OfType<PluginEntry>();
    foreach (PluginEntry plugin in plugins) {
      ReleaseEntry? release = plugin.LatestCompatible(build);
      if (release != null) {
        updates.Add(
          new IdeCompatibleUpdate(
            release.UpdateId,
            plugin.NumericId,
            release.Release.Version,
            plugin.Id
          )
        );
      }
    }

    return updates;
  }

  private static async Task<IResult> NearestUpdates(HttpRequest request,
    PluginCatalog catalog) {
    CompatibleUpdateRequest? body = await ReadBodyAsync(request);
    if (body == null) {
      return Results.BadRequest("Expected a JSON body.");
    }

    BuildNumber? build = BuildNumber.TryParse(body.Build) ?? request.Build();
    List<NearestUpdate> result = new();
    foreach (string id in body.PluginXmlIds ?? []) {
      PluginEntry? plugin = catalog.Snapshot.FindPlugin(id);
      if (plugin == null) {
        continue;
      }

      ReleaseEntry? compatible = plugin.LatestCompatible(build);
      result.Add(ToNearestUpdate(compatible ?? plugin.Latest, compatible != null));
    }

    return Results.Json(result);
  }

  private static NearestUpdate ToNearestUpdate(ReleaseEntry release,
                                               bool compatible) {
    return new NearestUpdate(
      release.UpdateId,
      release.Plugin.Id,
      release.Release.Version,
      [],
      new Dictionary<string, long>(),
      compatible
    );
  }

  private static IResult UpdateMeta(int pluginId, int updateId,
                                    PluginCatalog catalog) {
    ReleaseEntry? entry = catalog.Snapshot.FindUpdate(updateId);
    if (entry == null || entry.Plugin.NumericId != pluginId) {
      return Results.NotFound();
    }

    PluginRelease release = entry.Release;
    return Results.Json(
      new UpdateMetadata(
        entry.UpdateId,
        release.PluginId,
        release.Name,
        release.Description ?? "",
        release.Vendor ?? "",
        release.Vendor ?? "",
        release.Tags,
        release.Version,
        release.ChangeNotes ?? "",
        release.Dependencies,
        release.OptionalDependencies,
        release.Dependencies,
        release.OptionalDependencies,
        release.Modules,
        release.SinceBuild,
        release.UntilBuild,
        release.ProductCode,
        release.Url,
        release.Size
      )
    );
  }

  private static IResult PluginMeta(int pluginId, PluginCatalog catalog) {
    PluginEntry? plugin = catalog.Snapshot.FindPlugin(pluginId);
    if (plugin == null) {
      return Results.NotFound();
    }

    PluginRelease latest = plugin.Latest.Release;
    return Results.Json(
      new PluginMetadata(
        [],
        latest.Vendor == null
          ? null
          : new PluginVendorMetadata(
            latest.Vendor,
            latest.VendorUrl,
            false,
            false
          ),
        latest.Url,
        null,
        null,
        null,
        null
      )
    );
  }

  private static IResult PluginXmlIds(HttpContext context,
                                      PluginCatalog catalog) {
    return IdList(context, catalog, _ => true);
  }

  private static IResult JetBrainsPluginXmlIds(HttpContext context,
                                               PluginCatalog catalog) {
    return IdList(
      context,
      catalog,
      p => p.Vendor?.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) ==
           true
    );
  }

  private static IResult IdList(HttpContext context, PluginCatalog catalog,
                                Func<PluginEntry, bool> filter) {
    CatalogSnapshot snapshot = catalog.Snapshot;
    if (context.IsNotModified(snapshot.ETag)) {
      return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Json(
      snapshot.Plugins
              .Where(filter)
              .Select(p => p.Id)
              .Order(StringComparer.Ordinal)
              .ToList()
    );
  }

  private static async Task BrokenPlugins(HttpContext context,
                                          IBlobStore store) {
    // Without a mirrored list the IDE keeps the list bundled with it, which
    // is safer than claiming that nothing is broken.
    BlobInfo? info = await store.GetInfoAsync(
                       StorageLayout.C_BROKEN_PLUGINS_KEY,
                       context.RequestAborted
                     );
    if (info == null) {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }

    string etag = $"\"{info.Length:x}-{info.LastModified.ToUnixTimeSeconds():x}\"";
    if (context.IsNotModified(etag)) {
      context.Response.StatusCode = StatusCodes.Status304NotModified;
      return;
    }

    context.Response.ContentType = C_JSON_;
    context.Response.ContentLength = info.Length;
    await store.CopyToAsync(
      StorageLayout.C_BROKEN_PLUGINS_KEY,
      context.Response.Body,
      context.RequestAborted
    );
  }

  private static async Task Icon(HttpContext context, PluginCatalog catalog,
                                 IBlobStore store) {
    string? plugin_id = context.Request.QueryString("pluginId");
    PluginEntry? plugin = plugin_id == null
                            ? null
                            : catalog.Snapshot.FindPlugin(plugin_id);
    PluginRelease? release = plugin?.Releases
                                   .Select(r => r.Release)
                                   .FirstOrDefault(r => r.HasIcon);
    if (release == null) {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }

    bool dark = string.Equals(
                  context.Request.QueryString("theme"),
                  "DARCULA",
                  StringComparison.OrdinalIgnoreCase
                ) &&
                release.HasDarkIcon;
    string key = dark ? release.DarkIconKey : release.IconKey;
    string etag = $"\"{release.Sha256[..16]}{(dark ? "-d" : "")}\"";
    if (context.IsNotModified(etag)) {
      context.Response.StatusCode = StatusCodes.Status304NotModified;
      return;
    }

    byte[]? svg = await store.ReadAllBytesAsync(key, context.RequestAborted);
    if (svg == null) {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }

    context.Response.ContentType = "image/svg+xml";
    context.Response.Headers.CacheControl = "public, max-age=3600";
    await context.Response.Body.WriteAsync(svg, context.RequestAborted);
  }

  private static IResult PluginManagerDownload(HttpRequest request,
                                               PluginCatalog catalog,
                                               ILoggerFactory logger_factory) {
    string? action = request.QueryString("action");
    string? plugin_id = request.QueryString("id");
    if ((action != null &&
         !string.Equals(action, "download", StringComparison.OrdinalIgnoreCase)) ||
        plugin_id == null) {
      return Results.NotFound();
    }

    PluginEntry? plugin = catalog.Snapshot.FindPlugin(plugin_id);
    string? version = request.QueryString("version");
    BuildNumber? build = request.Build();
    ReleaseEntry? release = version != null
                              ? plugin?.Releases.FirstOrDefault(
                                r => r.Release.Version == version
                              )
                              : plugin?.LatestCompatible(build);
    if (release == null) {
      logger_factory.CreateLogger(typeof(MarketplaceEndpoints))
                    .LogInformation(
                      "No release of {PluginId} for build {Build}",
                      plugin_id,
                      build?.ToString() ?? "(any)"
                    );
      return Results.NotFound(
        plugin == null
          ? $"Plugin '{plugin_id}' is not available on this server."
          : $"No version of '{plugin_id}' is compatible with {build}."
      );
    }

    // A relative redirect keeps the original scheme and host, so this works
    // unchanged behind TLS-terminating proxies.
    return Results.Redirect(HttpHelpers.FileUrl(release));
  }

  private static IResult WebsiteDownload(HttpRequest request,
                                         PluginCatalog catalog) {
    CatalogSnapshot snapshot = catalog.Snapshot;
    ReleaseEntry? release = null;
    int update_id = request.QueryInt("updateId", -1);
    if (update_id >= 0) {
      release = snapshot.FindUpdate(update_id);
    } else if (request.QueryString("pluginId") is { } plugin_id) {
      PluginEntry? plugin = snapshot.FindPlugin(plugin_id);
      string? version = request.QueryString("version");
      release = version == null
                  ? plugin?.LatestCompatible(request.Build())
                  : plugin?.Releases.FirstOrDefault(
                    r => r.Release.Version == version
                  );
    }

    return release == null
             ? Results.NotFound()
             : Results.Redirect(HttpHelpers.FileUrl(release));
  }

  private static async Task DownloadFile(HttpContext context, int pluginId,
                                         int updateId, string fileName,
                                         PluginCatalog catalog,
                                         IBlobStore store,
                                         DownloadStatistics statistics,
                                         ILoggerFactory logger_factory) {
    ReleaseEntry? entry = catalog.Snapshot.FindUpdate(updateId);
    // Block map / hash requests (incremental updates) intentionally 404; the
    // IDE then falls back to a full download.
    if (entry == null ||
        entry.Plugin.NumericId != pluginId ||
        entry.Release.FileName != fileName) {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }

    await SendArtifactAsync(context, entry.Release, store, statistics,
                            logger_factory);
  }

  internal static async Task SendArtifactAsync(HttpContext context,
                                               PluginRelease release,
                                               IBlobStore store,
                                               DownloadStatistics statistics,
                                               ILoggerFactory logger_factory) {
    CancellationToken ct = context.RequestAborted;
    BlobInfo? info = await store.GetInfoAsync(release.ArtifactKey, ct);
    if (info == null) {
      logger_factory.CreateLogger(typeof(MarketplaceEndpoints))
                    .LogError(
                      "Artifact {Key} of {PluginId} {Version} is missing from storage",
                      release.ArtifactKey,
                      release.PluginId,
                      release.Version
                    );
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }

    string etag = $"\"{release.Sha256}\"";
    if (context.IsNotModified(etag)) {
      context.Response.StatusCode = StatusCodes.Status304NotModified;
      return;
    }

    HttpResponse response = context.Response;
    response.ContentType = release.IsJar
                             ? "application/java-archive"
                             : "application/zip";
    response.ContentLength = info.Length;
    // The IDE derives the installed file name from this header or the URL.
    response.Headers.ContentDisposition = new ContentDispositionHeaderValue(
      "attachment"
    ) { FileName = release.FileName }.ToString();
    response.Headers.LastModified = release.UploadedAt.ToString("R");
    if (HttpMethods.IsHead(context.Request.Method)) {
      return;
    }

    statistics.Increment(release.PluginId);
    await store.CopyToAsync(release.ArtifactKey, response.Body, ct);
  }

  private static async Task<CompatibleUpdateRequest?> ReadBodyAsync(
    HttpRequest request) {
    try {
      return await JsonSerializer.DeserializeAsync<CompatibleUpdateRequest>(
               request.Body,
               cancellationToken: request.HttpContext.RequestAborted
             );
    } catch (JsonException) {
      return null;
    }
  }
}
