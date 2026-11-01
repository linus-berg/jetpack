using System.Globalization;
using Jetpack.Api.Compatibility;
using Jetpack.Api.Plugins;
using Microsoft.Extensions.Primitives;

namespace Jetpack.Api.Endpoints;

internal static class HttpHelpers {
  public static BuildNumber? Build(this HttpRequest request) {
    return BuildNumber.TryParse(request.Query["build"].FirstOrDefault());
  }

  /// <summary>The <c>os</c> / <c>arch</c> parameters sent by IDEs 2025.3+.</summary>
  public static ClientPlatform Platform(this HttpRequest request) {
    return ClientPlatform.Parse(
      request.Query["os"].FirstOrDefault(),
      request.Query["arch"].FirstOrDefault()
    );
  }

  public static string? QueryString(this HttpRequest request, string name) {
    string? value = request.Query[name].FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? null : value;
  }

  public static IReadOnlyList<string> QueryValues(this HttpRequest request,
                                                  params string[] names) {
    return names.SelectMany(n => request.Query[n])
                .OfType<string>()
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
  }

  public static bool QueryFlag(this HttpRequest request, string name) {
    return bool.TryParse(request.Query[name].FirstOrDefault(), out bool flag) &&
           flag;
  }

  public static int QueryInt(this HttpRequest request, string name,
                             int fallback) {
    return int.TryParse(
             request.Query[name].FirstOrDefault(),
             NumberStyles.Integer,
             CultureInfo.InvariantCulture,
             out int value
           )
             ? value
             : fallback;
  }

  /// <summary>
  ///   Sets the ETag and reports whether the client already has this version
  ///   (the IDE caches Marketplace files and revalidates with If-None-Match).
  /// </summary>
  public static bool IsNotModified(this HttpContext context, string etag) {
    context.Response.Headers.ETag = etag;
    StringValues header = context.Request.Headers.IfNoneMatch;
    foreach (string? value in header) {
      if (value == null) {
        continue;
      }

      foreach (string candidate in value.Split(',')) {
        string trimmed = candidate.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal)) {
          trimmed = trimmed[2..];
        }

        if (trimmed == "*" || trimmed == etag) {
          return true;
        }
      }
    }

    return false;
  }

  public static string FileUrl(ReleaseEntry release) {
    return $"/files/{release.Plugin.NumericId}/{release.UpdateId}/" +
           Uri.EscapeDataString(release.Release.FileName);
  }

  public static long ToUnixMilliseconds(DateTimeOffset value) {
    return value.ToUnixTimeMilliseconds();
  }
}
