using AngleSharp.Dom;
using Ganss.Xss;
using Microsoft.AspNetCore.Html;

namespace Jetpack.Api.Web;

/// <summary>
///   Plugin descriptions and change notes are HTML written by plugin authors.
///   They are rendered inline, so they are reduced to safe formatting markup:
///   no scripts, event handlers, styles, classes or forms. Images are dropped
///   because they almost always point at the internet, which is unreachable
///   offline.
/// </summary>
public sealed class DescriptionSanitizer {
  private readonly HtmlSanitizer sanitizer_;

  public DescriptionSanitizer() {
    sanitizer_ = new HtmlSanitizer();
    foreach (string tag in new[] {
               "img", "picture", "source", "video", "audio", "form", "input",
               "button", "select", "textarea", "label", "fieldset", "style"
             }) {
      sanitizer_.AllowedTags.Remove(tag);
    }

    foreach (string attribute in new[] { "style", "class", "id", "name" }) {
      sanitizer_.AllowedAttributes.Remove(attribute);
    }

    sanitizer_.AllowedSchemes.Clear();
    sanitizer_.AllowedSchemes.Add("http");
    sanitizer_.AllowedSchemes.Add("https");
    sanitizer_.AllowedSchemes.Add("mailto");
    sanitizer_.AllowedCssProperties.Clear();
    sanitizer_.PostProcessNode += (_, e) => {
      if (e.Node is IElement { LocalName: "a" } link) {
        link.SetAttribute("target", "_blank");
        link.SetAttribute("rel", "noopener noreferrer nofollow");
      }
    };
  }

  public IHtmlContent Sanitize(string? html) {
    return string.IsNullOrWhiteSpace(html)
             ? HtmlString.Empty
             : new HtmlString(sanitizer_.Sanitize(html));
  }
}
