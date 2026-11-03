using Jetpack.Api.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Pages;

public sealed class UploadModel : PageModel {
  private readonly JetpackOptions options_;

  public UploadModel(IOptions<JetpackOptions> options) {
    options_ = options.Value;
  }

  public long MaxUploadBytes => options_.MaxUploadBytes;

  /// <summary>Uploads need a key unless anonymous uploads are explicitly allowed.</summary>
  public bool KeyRequired => !string.IsNullOrEmpty(options_.ApiKey);

  public bool UploadsDisabled =>
    string.IsNullOrEmpty(options_.ApiKey) && !options_.AllowAnonymousUploads;

  public string ServerUrl { get; private set; } = "";

  public void OnGet() {
    ViewData["Title"] = "Upload plugins";
    ViewData["Nav"] = "upload";
    ServerUrl = (options_.PublicUrl ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
  }
}
