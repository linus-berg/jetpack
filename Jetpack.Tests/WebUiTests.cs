using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace Jetpack.Tests;

public sealed partial class WebUiTests : IAsyncLifetime {
  private static readonly string[] S_PAGES_ = [
    "/",
    "/?q=example&sort=downloads",
    "/upload",
    "/connect",
    "/plugin/index?xmlId=com.example.ui"
  ];

  private readonly JetpackFactory factory_ = new();
  private HttpClient client_ = null!;

  public async Task InitializeAsync() {
    client_ = factory_.CreateRawClient();
    await factory_.UploadOkAsync(
      TestPlugins.Jar(
        TestPlugins.PluginXml(
          "com.example.ui",
          "2.1.0",
          "241",
          "252.*",
          "UI Example",
          "<depends optional=\"true\">com.example.dep</depends>\n" +
          "<depends>com.example.missing</depends>\n"
        )
      ),
      "ui-2.1.0.jar",
      "&tags=Tools"
    );
    await factory_.UploadOkAsync(
      TestPlugins.Jar(
        TestPlugins.PluginXml("com.example.dep", "1.0", name: "Dependency Helper"),
        icons: false
      ),
      "dep-1.0.jar"
    );
  }

  public async Task DisposeAsync() {
    client_.Dispose();
    await factory_.DisposeAsync();
  }

  [Fact]
  public async Task PagesRenderWithTheirContent() {
    string index = await GetPageAsync("/");
    Assert.Contains("UI Example", index);
    Assert.Contains("Dependency Helper", index);
    Assert.Contains("v2.1.0", index);

    string plugin = await GetPageAsync("/plugin/index?xmlId=com.example.ui");
    Assert.Contains("UI Example", plugin);
    Assert.Contains("ui-2.1.0.jar", plugin);
    Assert.Contains("241 – 252.*", plugin);
    // Dependencies hosted here are linked, others are listed.
    Assert.Contains("/plugin/index?xmlId=com.example.dep", plugin);
    Assert.Contains("com.example.missing", plugin);

    Assert.Contains("idea.plugins.host=http://localhost", await GetPageAsync("/connect"));
    Assert.Contains("data-jp-upload-form", await GetPageAsync("/upload"));
  }

  [Fact]
  public async Task PagesOnlyUseAssetsServedByJetpack() {
    // The UI must work without internet access: every script, stylesheet and
    // image must come from this server, and exist.
    HashSet<string> assets = new(StringComparer.Ordinal);
    foreach (string page in S_PAGES_) {
      string html = await GetPageAsync(page);
      foreach (Match match in AssetReference().Matches(html)) {
        string url = WebUtility.HtmlDecode(match.Groups["url"].Value);
        Assert.False(
          url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
          url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
          url.StartsWith("//", StringComparison.Ordinal),
          $"{page} references external asset {url}"
        );
        if (!url.StartsWith("data:", StringComparison.Ordinal)) {
          assets.Add(url);
        }
      }
    }

    // URLs carry a content fingerprint (MapStaticAssets), e.g. site.abc123.css.
    Assert.Contains(assets, a => Fingerprinted(a, "/lib/bootstrap/dist/css/bootstrap.min", ".css"));
    Assert.Contains(assets, a => Fingerprinted(a, "/lib/bootstrap/dist/js/bootstrap.bundle.min", ".js"));
    Assert.Contains(assets, a => Fingerprinted(a, "/css/site", ".css"));
    Assert.Contains(assets, a => Fingerprinted(a, "/js/site", ".js"));
    Assert.Contains(assets, a => Fingerprinted(a, "/js/theme", ".js"));
    foreach (string asset in assets) {
      HttpResponseMessage response = await client_.GetAsync(asset);
      Assert.True(response.IsSuccessStatusCode, $"{asset} -> {response.StatusCode}");
    }
  }

  [Fact]
  public async Task PagesSendAStrictContentSecurityPolicy() {
    HttpResponseMessage response = await client_.GetAsync("/");
    string policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
    Assert.Contains("default-src 'self'", policy);
    Assert.Contains("script-src 'self'", policy);
    Assert.DoesNotContain("unsafe-inline", policy);
    // No inline scripts or style attributes, which that policy would block.
    string html = await response.Content.ReadAsStringAsync();
    Assert.DoesNotMatch(InlineScript(), html);
    Assert.DoesNotContain(" style=\"", html);
  }

  [Fact]
  public async Task PluginDescriptionsAreSanitized() {
    await factory_.UploadOkAsync(
      TestPlugins.Jar(
        "<idea-plugin><id>com.example.xss</id><name>XSS Test</name><version>1</version>" +
        "<description><![CDATA[<p>Safe <b>bold</b> <a href=\"https://example.com\">link</a></p>" +
        "<script>alert('s')</script><img src=x onerror=alert('i')>" +
        "<a href=\"javascript:alert('j')\">bad</a><p style=\"color:red\" onclick=\"alert('c')\">styled</p>" +
        "]]></description></idea-plugin>"
      ),
      "xss.jar"
    );
    string html = await GetPageAsync("/plugin/index?xmlId=com.example.xss");
    Assert.Contains("<b>bold</b>", html);
    Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
    Assert.DoesNotContain("alert(", html);
    Assert.DoesNotContain("onerror", html);
    Assert.DoesNotContain("javascript:", html);
    Assert.DoesNotContain("color:red", html);
  }

  [Fact]
  public async Task SearchFiltersAndReportsMisses() {
    string hit = await GetPageAsync("/?q=helper");
    Assert.Contains("Dependency Helper", hit);
    Assert.DoesNotContain(">UI Example<", hit);
    Assert.Contains("No plugins match", await GetPageAsync("/?q=nothing-like-this"));
  }

  [Fact]
  public async Task PluginsWithoutIconsGetAPlaceholder() {
    string html = await GetPageAsync("/");
    Assert.Contains("jp-plugin-icon-fallback", html);
    Assert.Contains("/api/icon?pluginId=com.example.ui", html);
  }

  [Fact]
  public async Task UnknownPluginsAreNotFound() {
    HttpResponseMessage response = await client_.GetAsync("/plugin/index?xmlId=nope");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Contains("Plugin not found", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task UploadPageWarnsWhenUploadsAreDisabled() {
    await using NoApiKeyFactory factory = new();
    using HttpClient client = factory.CreateRawClient();
    string html = await client.GetStringAsync("/upload");
    Assert.Contains("Uploads are disabled", html);
  }

  private static bool Fingerprinted(string url, string stem, string extension) {
    string path = url.Split('?')[0];
    return path == stem + extension ||
           Regex.IsMatch(path, $"^{Regex.Escape(stem)}\\.[a-z0-9]+{Regex.Escape(extension)}$");
  }

  private async Task<string> GetPageAsync(string url) {
    HttpResponseMessage response = await client_.GetAsync(url);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{url} -> {response.StatusCode}: {body}");
    Assert.StartsWith("text/html", response.Content.Headers.ContentType!.MediaType);
    return body;
  }

  [GeneratedRegex("<(?:script|link|img)\\b[^>]*?\\b(?:src|href)=\"(?<url>[^\"]+)\"",
                  RegexOptions.IgnoreCase)]
  private static partial Regex AssetReference();

  [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
  private static partial Regex InlineScript();

  private sealed class NoApiKeyFactory : JetpackFactory {
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
      base.ConfigureWebHost(builder);
      builder.UseSetting("Jetpack:ApiKey", "");
    }
  }
}
