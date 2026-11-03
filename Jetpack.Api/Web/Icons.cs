using Microsoft.AspNetCore.Html;

namespace Jetpack.Api.Web;

/// <summary>
///   Small inline SVG icon set (stroke icons on a 24x24 grid). Inline SVG keeps
///   the UI self-contained: no icon fonts or external assets.
/// </summary>
public static class Icons {
  private static readonly Dictionary<string, string> S_PATHS_ = new() {
    ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-3.5-3.5\"/>",
    ["upload"] =
      "<path d=\"M12 15V4m0 0L8 8m4-4 4 4\"/><path d=\"M4 15v4a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-4\"/>",
    ["download"] =
      "<path d=\"M12 4v11m0 0-4-4m4 4 4-4\"/><path d=\"M4 15v4a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-4\"/>",
    ["copy"] =
      "<rect x=\"9\" y=\"9\" width=\"11\" height=\"11\" rx=\"2\"/><path d=\"M5 15V5a2 2 0 0 1 2-2h8\"/>",
    ["check"] = "<path d=\"m5 12 5 5 9-10\"/>",
    ["trash"] =
      "<path d=\"M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3\"/>",
    ["plug"] = "<path d=\"M9 2v5M15 2v5M6 7h12v4a6 6 0 0 1-12 0V7zM12 17v5\"/>",
    ["external"] =
      "<path d=\"M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5\"/>",
    ["sun"] =
      "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4\"/>",
    ["moon"] = "<path d=\"M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z\"/>",
    ["package"] = "<path d=\"M21 8 12 3 3 8v8l9 5 9-5V8z\"/><path d=\"m3 8 9 5 9-5M12 13v8\"/>",
    ["layers"] = "<path d=\"m12 3 9 5-9 5-9-5 9-5z\"/><path d=\"m3 13 9 5 9-5\"/>",
    ["server"] =
      "<rect x=\"3\" y=\"4\" width=\"18\" height=\"7\" rx=\"2\"/><rect x=\"3\" y=\"13\" width=\"18\" height=\"7\" rx=\"2\"/><path d=\"M7 7.5h.01M7 16.5h.01\"/>",
    ["globe"] =
      "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18\"/>",
    ["terminal"] = "<path d=\"m4 17 6-5-6-5M12 19h8\"/>",
    ["list"] = "<path d=\"M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01\"/>",
    ["alert"] =
      "<path d=\"M12 9v4M12 17h.01\"/><path d=\"M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z\"/>",
    ["arrow-left"] = "<path d=\"M19 12H5m0 0 6-6m-6 6 6 6\"/>",
    ["tag"] =
      "<path d=\"M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z\"/><path d=\"M7.5 7.5h.01\"/>",
    ["key"] =
      "<circle cx=\"7.5\" cy=\"15.5\" r=\"4.5\"/><path d=\"m10.7 12.3 9.3-9.3M16 7l3 3M14 9l2 2\"/>",
    ["cpu"] =
      "<rect x=\"6\" y=\"6\" width=\"12\" height=\"12\" rx=\"2\"/><path d=\"M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4\"/>",
    ["calendar"] =
      "<rect x=\"3\" y=\"5\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M16 3v4M8 3v4M3 11h18\"/>",
    ["file"] = "<path d=\"M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z\"/><path d=\"M14 3v5h5\"/>",
    ["x"] = "<path d=\"M18 6 6 18M6 6l12 12\"/>",
    ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v6M12 7.5h.01\"/>",
    ["shield"] = "<path d=\"M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6l-8-3z\"/><path d=\"m9 12 2 2 4-4\"/>",
    ["code"] = "<path d=\"m8 8-5 4 5 4M16 8l5 4-5 4M14 4l-4 16\"/>",
    ["refresh"] = "<path d=\"M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4\"/>"
  };

  public static IHtmlContent Render(string name, string css_class = "jp-icon") {
    string paths = S_PATHS_.GetValueOrDefault(name) ??
                   throw new ArgumentException($"Unknown icon '{name}'.", nameof(name));
    return new HtmlString(
      $"<svg class=\"{css_class}\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" " +
      "fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" " +
      $"stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{paths}</svg>"
    );
  }
}
