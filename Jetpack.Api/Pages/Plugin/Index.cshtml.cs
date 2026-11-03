using Jetpack.Api.Plugins;
using Jetpack.Api.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jetpack.Api.Pages.Plugin;

/// <summary>
///   Plugin details. Served at <c>/plugin/index?xmlId=...</c>, which is the
///   "plugin homepage" link inside the IDE.
/// </summary>
public sealed class IndexModel : PageModel {
  private readonly PluginCatalog catalog_;
  private readonly DescriptionSanitizer sanitizer_;
  private readonly DownloadStatistics statistics_;

  public IndexModel(PluginCatalog catalog, DownloadStatistics statistics,
                    DescriptionSanitizer sanitizer) {
    catalog_ = catalog;
    statistics_ = statistics;
    sanitizer_ = sanitizer;
  }

  [BindProperty(SupportsGet = true, Name = "xmlId")]
  public string? XmlId { get; set; }

  [BindProperty(SupportsGet = true, Name = "id")]
  public string? Id { get; set; }

  public string? RequestedId => XmlId ?? Id;

  public PluginEntry? Plugin { get; private set; }
  public PluginRelease Latest => Plugin!.Latest.Release;
  public long Downloads { get; private set; }
  public long TotalSize { get; private set; }
  public IHtmlContent Description { get; private set; } = HtmlString.Empty;
  public IHtmlContent ChangeNotes { get; private set; } = HtmlString.Empty;

  /// <summary>Required dependencies; <c>Hosted</c> is set when the plugin is on this server.</summary>
  public IReadOnlyList<Dependency> Dependencies { get; private set; } = [];

  public IReadOnlyList<Dependency> OptionalDependencies { get; private set; } = [];

  public IActionResult OnGet() {
    ViewData["Nav"] = "plugins";
    CatalogSnapshot snapshot = catalog_.Snapshot;
    Plugin = RequestedId == null ? null : snapshot.FindPlugin(RequestedId);
    if (Plugin == null) {
      ViewData["Title"] = "Plugin not found";
      Response.StatusCode = StatusCodes.Status404NotFound;
      return Page();
    }

    ViewData["Title"] = Plugin.Name;
    Downloads = statistics_.Get(Plugin.Id);
    TotalSize = Plugin.Releases.Sum(r => r.Release.Size);
    Description = sanitizer_.Sanitize(Latest.Description);
    ChangeNotes = sanitizer_.Sanitize(Latest.ChangeNotes);
    Dependencies = Latest.Dependencies.Select(d => Describe(snapshot, d)).ToList();
    OptionalDependencies =
      Latest.OptionalDependencies.Select(d => Describe(snapshot, d)).ToList();
    return Page();
  }

  private static Dependency Describe(CatalogSnapshot snapshot, string id) {
    PluginEntry? hosted = snapshot.FindPlugin(id);
    string kind = id.StartsWith("com.intellij.modules.", StringComparison.Ordinal)
                    ? "Platform module"
                    : hosted != null
                      ? "Available here"
                      : "Bundled with the IDE or not on this server";
    return new Dependency(id, hosted?.Name, hosted != null, kind);
  }

  public sealed record Dependency(string Id, string? Name, bool Hosted, string Kind);
}
