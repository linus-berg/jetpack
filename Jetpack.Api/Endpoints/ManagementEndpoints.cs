using System.Text.Json;
using Jetpack.Api.Configuration;
using Jetpack.Api.Plugins;
using Jetpack.Api.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Jetpack.Api.Endpoints;

/// <summary>Uploading, listing and deleting plugins.</summary>
public static class ManagementEndpoints {
  public static void MapManagementEndpoints(this IEndpointRouteBuilder app) {
    // "/api/plugins/upload" is the URL used by the previous Jetpack version.
    app.MapPost("/api/plugins/upload", Upload)
       .AddEndpointFilter<ApiKeyFilter>()
       .DisableAntiforgery();
    app.MapPost("/api/plugins", Upload)
       .AddEndpointFilter<ApiKeyFilter>()
       .DisableAntiforgery();
    app.MapGet("/api/plugins", List);
    app.MapGet("/api/plugins/{pluginId}", Detail);
    app.MapDelete("/api/plugins/{pluginId}", DeletePlugin)
       .AddEndpointFilter<ApiKeyFilter>();
    app.MapDelete("/api/plugins/{pluginId}/{version}", DeleteVersion)
       .AddEndpointFilter<ApiKeyFilter>();
    app.MapPut("/files/brokenPlugins.json", PutBrokenPlugins)
       .AddEndpointFilter<ApiKeyFilter>();
    app.MapPost("/api/catalog/refresh", Refresh)
       .AddEndpointFilter<ApiKeyFilter>();
    app.MapGet("/health", Health);
  }

  /// <summary>
  ///   Accepts either a raw archive body (<c>--data-binary @plugin.zip</c>,
  ///   optional <c>?filename=</c>) or <c>multipart/form-data</c> with one or
  ///   more files. <c>?force=true</c> replaces an existing version and
  ///   <c>?tags=Languages,Tools</c> sets Marketplace tags.
  /// </summary>
  private static async Task<IResult> Upload(HttpContext context,
                                            PluginUploadService uploads,
                                            PluginCatalog catalog,
                                            IOptions<JetpackOptions> options) {
    IHttpMaxRequestBodySizeFeature? limit =
      context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) {
      limit.MaxRequestBodySize = options.Value.MaxUploadBytes;
    }

    HttpRequest request = context.Request;
    bool force = request.QueryFlag("force") || request.QueryFlag("overwrite");
    List<string> tags = request.QueryValues("tags", "tag")
                               .SelectMany(t => t.Split(','))
                               .Select(t => t.Trim())
                               .Where(t => t.Length > 0)
                               .ToList();
    CancellationToken ct = context.RequestAborted;

    if (!MediaTypeHeaderValue.TryParse(request.ContentType,
                                       out MediaTypeHeaderValue? media_type) ||
        !media_type.MediaType.Equals("multipart/form-data",
                                     StringComparison.OrdinalIgnoreCase)) {
      string? file_name = request.QueryString("filename") ??
                          request.Headers["X-File-Name"].FirstOrDefault();
      UploadOutcome outcome = await uploads.UploadAsync(
                                request.Body,
                                file_name,
                                force,
                                tags,
                                ct
                              );
      return Results.Json(
        ToResponse(outcome, catalog),
        statusCode: StatusCode(outcome.Status)
      );
    }

    string? boundary = HeaderUtilities.RemoveQuotes(media_type.Boundary).Value;
    if (string.IsNullOrEmpty(boundary)) {
      return Results.BadRequest("Missing multipart boundary.");
    }

    MultipartReader reader = new(boundary, request.Body) {
      BodyLengthLimit = options.Value.MaxUploadBytes
    };
    List<UploadOutcome> outcomes = new();
    while (await reader.ReadNextSectionAsync(ct) is { } section) {
      if (!ContentDispositionHeaderValue.TryParse(
            section.ContentDisposition,
            out ContentDispositionHeaderValue? disposition
          ) ||
          !disposition.IsFileDisposition()) {
        continue;
      }

      string? file_name = disposition.FileNameStar.Value ??
                          disposition.FileName.Value;
      outcomes.Add(await uploads.UploadAsync(section.Body, file_name, force,
                                             tags, ct));
    }

    if (outcomes.Count == 0) {
      return Results.BadRequest("The multipart request contains no files.");
    }

    bool any_success = outcomes.Any(o => IsSuccess(o.Status));
    return Results.Json(
      new { results = outcomes.Select(o => ToResponse(o, catalog)).ToList() },
      statusCode: any_success ? StatusCodes.Status200OK
                              : StatusCode(outcomes[0].Status)
    );
  }

  private static IResult List(PluginCatalog catalog,
                              DownloadStatistics statistics) {
    return Results.Json(
      catalog.Snapshot
             .Plugins
             .Select(p => ToPluginResponse(p, statistics))
             .ToList()
    );
  }

  private static IResult Detail(string pluginId, PluginCatalog catalog,
                                DownloadStatistics statistics) {
    PluginEntry? plugin = catalog.Snapshot.FindPlugin(pluginId);
    return plugin == null
             ? Results.NotFound()
             : Results.Json(ToPluginResponse(plugin, statistics));
  }

  private static async Task<IResult> DeletePlugin(string pluginId,
    PluginUploadService uploads, HttpContext context) {
    IReadOnlyList<PluginRelease> deleted = await uploads.DeleteAsync(
                                             pluginId,
                                             null,
                                             context.RequestAborted
                                           );
    return deleted.Count == 0
             ? Results.NotFound()
             : Results.Json(new { deleted = deleted.Select(r => r.Version) });
  }

  private static async Task<IResult> DeleteVersion(string pluginId,
    string version, PluginUploadService uploads, HttpContext context) {
    IReadOnlyList<PluginRelease> deleted = await uploads.DeleteAsync(
                                             pluginId,
                                             version,
                                             context.RequestAborted
                                           );
    return deleted.Count == 0
             ? Results.NotFound()
             : Results.Json(new { deleted = deleted.Select(r => r.Version) });
  }

  /// <summary>Stores a mirrored copy of the Marketplace's brokenPlugins.json.</summary>
  private static async Task<IResult> PutBrokenPlugins(HttpContext context,
    IBlobStore store) {
    using MemoryStream buffer = new();
    await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
    try {
      using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
      if (document.RootElement.ValueKind != JsonValueKind.Array) {
        return Results.BadRequest("Expected a JSON array.");
      }
    } catch (JsonException ex) {
      return Results.BadRequest($"Invalid JSON: {ex.Message}");
    }

    await store.PutBytesAsync(
      StorageLayout.C_BROKEN_PLUGINS_KEY,
      buffer.ToArray(),
      "application/json",
      context.RequestAborted
    );
    return Results.NoContent();
  }

  private static async Task<IResult> Refresh(PluginCatalog catalog,
                                             HttpContext context) {
    await catalog.RefreshAsync(context.RequestAborted);
    return Results.Json(
      new {
        plugins = catalog.Snapshot.Plugins.Count,
        releases = catalog.Snapshot.ReleaseCount
      }
    );
  }

  private static IResult Health(PluginCatalog catalog, IBlobStore store) {
    return Results.Json(
      new {
        status = catalog.IsLoaded ? "ok" : "loading",
        plugins = catalog.Snapshot.Plugins.Count,
        releases = catalog.Snapshot.ReleaseCount,
        storage = store.Description
      },
      statusCode: catalog.IsLoaded
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable
    );
  }

  private static object ToPluginResponse(PluginEntry plugin,
                                         DownloadStatistics statistics) {
    return new {
      id = plugin.Id,
      name = plugin.Name,
      vendor = plugin.Vendor,
      tags = plugin.Tags,
      downloads = statistics.Get(plugin.Id),
      marketplaceId = plugin.NumericId,
      releases = plugin.Releases.Select(ToReleaseResponse).ToList()
    };
  }

  private static object ToReleaseResponse(ReleaseEntry entry) {
    PluginRelease release = entry.Release;
    return new {
      version = release.Version,
      sinceBuild = release.SinceBuild,
      untilBuild = release.UntilBuild,
      fileName = release.FileName,
      kind = release.IsJar ? "jar" : "zip",
      size = release.Size,
      sha256 = release.Sha256,
      uploadedAt = release.UploadedAt,
      dependencies = release.Dependencies,
      optionalDependencies = release.OptionalDependencies,
      updateId = entry.UpdateId,
      downloadUrl = HttpHelpers.FileUrl(entry)
    };
  }

  private static object ToResponse(UploadOutcome outcome,
                                   PluginCatalog catalog) {
    PluginRelease? release = outcome.Release;
    ReleaseEntry? entry = release == null
                            ? null
                            : catalog.Snapshot.FindRelease(
                              release.PluginId,
                              release.Version
                            );
    return new {
      status = outcome.Status.ToString().ToLowerInvariant(),
      uploadedFileName = outcome.FileName,
      error = outcome.Error,
      plugin = release == null
                 ? null
                 : new {
                   id = release.PluginId,
                   name = release.Name,
                   version = release.Version,
                   vendor = release.Vendor,
                   sinceBuild = release.SinceBuild,
                   untilBuild = release.UntilBuild,
                   fileName = release.FileName,
                   kind = release.IsJar ? "jar" : "zip",
                   size = release.Size,
                   sha256 = release.Sha256,
                   downloadUrl = entry == null ? null : HttpHelpers.FileUrl(entry)
                 }
    };
  }

  private static bool IsSuccess(UploadStatus status) {
    return status is UploadStatus.CREATED or UploadStatus.REPLACED;
  }

  private static int StatusCode(UploadStatus status) {
    return status switch {
      UploadStatus.CREATED => StatusCodes.Status201Created,
      UploadStatus.REPLACED => StatusCodes.Status200OK,
      UploadStatus.CONFLICT => StatusCodes.Status409Conflict,
      UploadStatus.TOO_LARGE => StatusCodes.Status413PayloadTooLarge,
      _ => StatusCodes.Status400BadRequest
    };
  }
}
