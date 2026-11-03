using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jetpack.Api.Configuration;
using Jetpack.Api.Endpoints;
using Jetpack.Api.Plugins;
using Jetpack.Api.Storage;
using Jetpack.Api.Web;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;

namespace Jetpack.Api;

/// <summary>
///   Jetpack: an offline JetBrains plugin repository that speaks the
///   JetBrains Marketplace protocol.
/// </summary>
public class Program {
  // The web UI only ever loads resources from this server, which also keeps it
  // fully functional offline.
  private const string C_CONTENT_SECURITY_POLICY_ =
    "default-src 'self'; script-src 'self'; style-src 'self'; " +
    "img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
    "object-src 'none'; base-uri 'self'; form-action 'self'; " +
    "frame-ancestors 'none'";

  private static readonly string[] S_STATIC_PREFIXES_ =
    ["/lib/", "/css/", "/js/", "/img/", "/favicon"];

  public static async Task Main(string[] args) {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    LegacyConfiguration.Apply(builder.Configuration);

    builder.Services.Configure<JetpackOptions>(
      builder.Configuration.GetSection(JetpackOptions.C_SECTION)
    );
    builder.Services.Configure<StorageOptions>(
      builder.Configuration.GetSection(StorageOptions.C_SECTION)
    );
    builder.Services.AddSingleton<IBlobStore>(CreateBlobStore);
    builder.Services.AddSingleton<PluginCatalog>();
    builder.Services.AddSingleton<PluginUploadService>();
    builder.Services.AddSingleton<DownloadStatistics>();
    builder.Services.AddHostedService<CatalogBackgroundService>();
    builder.Services.AddSingleton<DescriptionSanitizer>();
    builder.Services.AddRazorPages();
    builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
    // Keep non-ASCII text (plugin names, "–") readable in the HTML; markup
    // characters are still escaped.
    builder.Services.Configure<WebEncoderOptions>(
      options => options.TextEncoderSettings =
                   new TextEncoderSettings(UnicodeRanges.All)
    );
    builder.Services.AddProblemDetails();
    builder.Services.ConfigureHttpJsonOptions(
      options => {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition =
          JsonIgnoreCondition.WhenWritingNull;
      }
    );

    WebApplication app = builder.Build();
    ILogger logger = app.Services
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Jetpack");
    await InitializeAsync(app.Services, logger);

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.Use(LogRequestsAsync);
    app.Use(AddPageSecurityHeadersAsync);
    app.MapStaticAssets();
    app.MapMarketplaceEndpoints();
    app.MapLegacyRepositoryEndpoints();
    app.MapManagementEndpoints();
    app.MapRazorPages().WithStaticAssets();
    app.MapFallback(
      (HttpContext context) => {
        // Anything an IDE asks for that we don't implement shows up here.
        logger.LogWarning(
          "No handler for {Method} {Path}{Query} (User-Agent: {UserAgent})",
          context.Request.Method,
          context.Request.Path,
          context.Request.QueryString,
          context.Request.Headers.UserAgent.ToString()
        );
        return Results.NotFound();
      }
    );

    await app.RunAsync();
  }

  private static IBlobStore CreateBlobStore(IServiceProvider services) {
    StorageOptions options = services
                             .GetRequiredService<IOptions<StorageOptions>>()
                             .Value;
    return options.GetProvider() switch {
      StorageProvider.S3 => new S3BlobStore(
        options.S3,
        services.GetRequiredService<ILogger<S3BlobStore>>()
      ),
      _ => new FileSystemBlobStore(options.Path)
    };
  }

  private static async Task InitializeAsync(IServiceProvider services,
                                            ILogger logger) {
    JetpackOptions options = services
                             .GetRequiredService<IOptions<JetpackOptions>>()
                             .Value;
    IBlobStore store = services.GetRequiredService<IBlobStore>();
    logger.LogInformation("Storage: {Storage}", store.Description);
    if (string.IsNullOrEmpty(options.ApiKey)) {
      logger.LogWarning(
        options.AllowAnonymousUploads
          ? "No API key configured: ANYONE can upload and delete plugins"
          : "No API key configured: uploads are disabled. Set " +
            "Jetpack__ApiKey (or JETPACK_API_KEY) to enable them"
      );
    }

    try {
      await store.InitializeAsync(CancellationToken.None);
      await services.GetRequiredService<PluginCatalog>()
                    .RefreshAsync(CancellationToken.None);
      await services.GetRequiredService<DownloadStatistics>()
                    .LoadAsync(CancellationToken.None);
    } catch (Exception ex) {
      // Keep serving; the background service retries until storage is back.
      logger.LogError(
        ex,
        "Could not load the plugin catalog from {Storage}; will retry",
        store.Description
      );
    }
  }

  private static Task AddPageSecurityHeadersAsync(HttpContext context,
                                                  RequestDelegate next) {
    context.Response.OnStarting(
      () => {
        HttpResponse response = context.Response;
        if (response.ContentType?.StartsWith(
              "text/html",
              StringComparison.OrdinalIgnoreCase
            ) ==
            true) {
          response.Headers.ContentSecurityPolicy = C_CONTENT_SECURITY_POLICY_;
          response.Headers.XContentTypeOptions = "nosniff";
          response.Headers.XFrameOptions = "DENY";
          response.Headers["Referrer-Policy"] = "same-origin";
        }

        return Task.CompletedTask;
      }
    );
    return next(context);
  }

  private static async Task LogRequestsAsync(HttpContext context,
                                             RequestDelegate next) {
    long start = Stopwatch.GetTimestamp();
    await next(context);
    string path = context.Request.Path.Value ?? "";
    if (context.Request.Path.StartsWithSegments("/health") ||
        S_STATIC_PREFIXES_.Any(
          p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)
        ) ||
        !context.RequestServices
                .GetRequiredService<IOptions<JetpackOptions>>()
                .Value
                .LogRequests) {
      return;
    }

    context.RequestServices
           .GetRequiredService<ILoggerFactory>()
           .CreateLogger("Jetpack.Requests")
           .LogInformation(
             "{Method} {Path}{Query} -> {Status} in {Elapsed:0} ms ({UserAgent})",
             context.Request.Method,
             context.Request.Path,
             context.Request.QueryString,
             context.Response.StatusCode,
             Stopwatch.GetElapsedTime(start).TotalMilliseconds,
             context.Request.Headers.UserAgent.ToString()
           );
  }
}
