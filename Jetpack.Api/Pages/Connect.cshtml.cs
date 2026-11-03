using Jetpack.Api.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Pages;

public sealed class ConnectModel : PageModel {
  private readonly JetpackOptions options_;

  public ConnectModel(IOptions<JetpackOptions> options) {
    options_ = options.Value;
  }

  public string ServerUrl { get; private set; } = "";
  public string ServerHost { get; private set; } = "";

  /// <summary>True when this page itself was reached through plugins.jetbrains.com.</summary>
  public bool ViaMarketplaceName { get; private set; }

  public void OnGet() {
    ViewData["Title"] = "Connect an IDE";
    ViewData["Nav"] = "connect";
    ServerUrl = (options_.PublicUrl ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
    ServerHost = Request.Host.Host;
    ViaMarketplaceName = string.Equals(
      Request.Host.Host,
      "plugins.jetbrains.com",
      StringComparison.OrdinalIgnoreCase
    );
  }
}
