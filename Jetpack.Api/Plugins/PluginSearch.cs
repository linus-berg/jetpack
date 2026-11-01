using Jetpack.Api.Compatibility;

namespace Jetpack.Api.Plugins;

public sealed class SearchQuery {
  public string? Text { get; init; }
  public string? OrderBy { get; init; }
  public bool Featured { get; init; }
  public IReadOnlyList<string> Tags { get; init; } = [];
  public IReadOnlyList<string> Organizations { get; init; } = [];
  public BuildNumber? Build { get; init; }
  public bool IncludeIncompatible { get; init; }
  public int Offset { get; init; }
  public int Max { get; init; } = 20;
}

/// <param name="Compatible">Newest release compatible with the query build, if any.</param>
public sealed record SearchHit(PluginEntry Plugin, ReleaseEntry? Compatible);

/// <summary>Implements the filtering and ordering of the Marketplace search API.</summary>
public static class PluginSearch {
  public static IReadOnlyList<SearchHit> Search(CatalogSnapshot snapshot,
                                                SearchQuery query,
                                                Func<string, long> downloads,
                                                IReadOnlyList<string> featured) {
    string? text = string.IsNullOrWhiteSpace(query.Text)
                     ? null
                     : query.Text.Trim().ToLowerInvariant();
    string[] tokens = text?.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries
                      ) ??
                      [];

    IEnumerable<PluginEntry> candidates = snapshot.Plugins;
    if (query.Featured && featured.Count > 0) {
      candidates = featured.Select(snapshot.FindPlugin).OfType<PluginEntry>();
    }

    List<(SearchHit Hit, int Score)> hits = new();
    foreach (PluginEntry plugin in candidates) {
      ReleaseEntry? compatible = plugin.LatestCompatible(query.Build);
      if (compatible == null && !query.IncludeIncompatible) {
        continue;
      }

      if (query.Tags.Count > 0 &&
          !query.Tags.Any(
            t => plugin.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)
          )) {
        continue;
      }

      if (query.Organizations.Count > 0 &&
          !query.Organizations.Any(
            o => string.Equals(o, plugin.Vendor,
                               StringComparison.OrdinalIgnoreCase)
          )) {
        continue;
      }

      int score = text == null ? 0 : Score(plugin, text, tokens);
      if (score < 0) {
        continue;
      }

      hits.Add((new SearchHit(plugin, compatible), score));
    }

    string order = (query.OrderBy ?? "").Trim().ToLowerInvariant();
    IOrderedEnumerable<(SearchHit Hit, int Score)> ordered;
    if (query.Featured && featured.Count > 0 && order.Length == 0) {
      // Keep the configured order.
      ordered = hits.OrderBy(_ => 0);
    } else {
      ordered = order switch {
        "update date" or "update+date" or "updated" or "publish date" =>
          hits.OrderByDescending(
            h => (h.Hit.Compatible ?? h.Hit.Plugin.Latest).Release.UploadedAt
          ),
        "downloads" or "rating" =>
          hits.OrderByDescending(h => downloads(h.Hit.Plugin.Id)),
        "name" => hits.OrderBy(
          h => h.Hit.Plugin.Name,
          StringComparer.OrdinalIgnoreCase
        ),
        _ when text != null => hits.OrderByDescending(h => h.Score)
                                   .ThenByDescending(
                                     h => downloads(h.Hit.Plugin.Id)
                                   ),
        _ => hits.OrderBy(
          h => h.Hit.Plugin.Name,
          StringComparer.OrdinalIgnoreCase
        )
      };
    }

    return ordered.ThenBy(h => h.Hit.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                  .Select(h => h.Hit)
                  .Skip(Math.Max(0, query.Offset))
                  .Take(Math.Clamp(query.Max, 0, 1000))
                  .ToList();
  }

  /// <returns>A relevance score, or -1 when the plugin does not match.</returns>
  private static int Score(PluginEntry plugin, string text, string[] tokens) {
    string name = plugin.Name.ToLowerInvariant();
    string id = plugin.Id.ToLowerInvariant();
    string vendor = plugin.Vendor?.ToLowerInvariant() ?? "";
    string tags = string.Join(' ', plugin.Tags).ToLowerInvariant();
    int score = 0;
    if (name == text || id == text) {
      score += 1000;
    } else if (name.StartsWith(text, StringComparison.Ordinal)) {
      score += 500;
    }

    foreach (string token in tokens) {
      int token_score = 0;
      if (name.Contains(token, StringComparison.Ordinal)) {
        token_score += 50;
      }

      if (id.Contains(token, StringComparison.Ordinal)) {
        token_score += 20;
      }

      if (vendor.Contains(token, StringComparison.Ordinal) ||
          tags.Contains(token, StringComparison.Ordinal)) {
        token_score += 10;
      }

      if (plugin.DescriptionText.Contains(token, StringComparison.Ordinal)) {
        token_score += 2;
      }

      if (token_score == 0) {
        return -1;
      }

      score += token_score;
    }

    return score;
  }
}
