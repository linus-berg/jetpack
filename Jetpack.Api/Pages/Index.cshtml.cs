using Jetpack.Api.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jetpack.Api.Pages;

/// <summary>Catalog overview with search and sorting.</summary>
public sealed class IndexModel : PageModel {
  public static readonly IReadOnlyList<(string Value, string Label)> S_SORTS = [
    ("", "Best match"),
    ("name", "Name"),
    ("downloads", "Most downloaded"),
    ("updated", "Recently updated")
  ];

  private readonly PluginCatalog catalog_;
  private readonly DownloadStatistics statistics_;

  public IndexModel(PluginCatalog catalog, DownloadStatistics statistics) {
    catalog_ = catalog;
    statistics_ = statistics;
  }

  [BindProperty(SupportsGet = true, Name = "q")]
  public string? Query { get; set; }

  [BindProperty(SupportsGet = true, Name = "sort")]
  public string? Sort { get; set; }

  public IReadOnlyList<PluginEntry> Plugins { get; private set; } = [];
  public int TotalPlugins { get; private set; }
  public int TotalReleases { get; private set; }
  public long TotalDownloads { get; private set; }
  public bool IsLoaded { get; private set; }

  public long Downloads(PluginEntry plugin) {
    return statistics_.Get(plugin.Id);
  }

  public void OnGet() {
    CatalogSnapshot snapshot = catalog_.Snapshot;
    IsLoaded = catalog_.IsLoaded;
    TotalPlugins = snapshot.Plugins.Count;
    TotalReleases = snapshot.ReleaseCount;
    TotalDownloads = snapshot.Plugins.Sum(p => statistics_.Get(p.Id));
    // Same filtering and ranking as the IDE's Marketplace search, but listing
    // every plugin regardless of IDE build.
    string? order = Sort switch {
      "name" => "name",
      "downloads" => "downloads",
      "updated" => "update date",
      _ => null
    };
    Plugins = PluginSearch.Search(
                            snapshot,
                            new SearchQuery {
                              Text = Query,
                              OrderBy = order,
                              IncludeIncompatible = true,
                              Max = 1000
                            },
                            statistics_.Get,
                            []
                          )
                          .Select(h => h.Plugin)
                          .ToList();
    ViewData["Nav"] = "plugins";
  }
}
