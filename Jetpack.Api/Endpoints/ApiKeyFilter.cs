using System.Security.Cryptography;
using System.Text;
using Jetpack.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Endpoints;

/// <summary>
///   Protects write endpoints. Accepts <c>X-Api-Key: key</c> or
///   <c>Authorization: Bearer key</c>.
/// </summary>
public sealed class ApiKeyFilter : IEndpointFilter {
  public const string C_HEADER = "X-Api-Key";

  public async ValueTask<object?> InvokeAsync(
    EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
    HttpContext http = context.HttpContext;
    JetpackOptions options = http.RequestServices
                                 .GetRequiredService<IOptions<JetpackOptions>>()
                                 .Value;
    if (string.IsNullOrEmpty(options.ApiKey)) {
      if (options.AllowAnonymousUploads) {
        return await next(context);
      }

      return Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Write access is disabled",
        detail: "No API key is configured on the server. Set " +
                "Jetpack__ApiKey (or JETPACK_API_KEY) to enable uploads."
      );
    }

    string? provided = http.Request.Headers[C_HEADER].FirstOrDefault();
    string? authorization = http.Request.Headers.Authorization.FirstOrDefault();
    if (provided == null &&
        authorization != null &&
        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
      provided = authorization["Bearer ".Length..].Trim();
    }

    if (string.IsNullOrEmpty(provided) || !Matches(provided, options.ApiKey)) {
      return Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid or missing API key",
        detail: $"Send the key in the {C_HEADER} header."
      );
    }

    return await next(context);
  }

  private static bool Matches(string provided, string expected) {
    // Hash first so the comparison is constant-time regardless of length.
    byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
    byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(a, b);
  }
}
