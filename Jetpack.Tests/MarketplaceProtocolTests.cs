using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Jetpack.Tests;

/// <summary>
///   Exercises the server the way IntelliJ-based IDEs call plugins.jetbrains.com
///   (request shapes taken from intellij-community MarketplaceRequests.kt).
/// </summary>
public sealed class MarketplaceProtocolTests : IAsyncLifetime {
  private const string C_BUILD_ = "IU-252.25557.131";
  private readonly JetpackFactory factory_ = new();
  private HttpClient client_ = null!;

  public async Task InitializeAsync() {
    client_ = factory_.CreateRawClient();
    // Three versions of one jar plugin with different compatibility ranges.
    await factory_.UploadOkAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.tool", "1.0.0", "241", "251.*")),
      "tool-1.0.0.jar"
    );
    await factory_.UploadOkAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.tool", "1.10.0", "252", "252.*")),
      "tool-1.10.0.jar"
    );
    await factory_.UploadOkAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.tool", "1.9.0", "252", "252.*")),
      "tool-1.9.0.jar"
    );
    // A zip plugin that requires a newer IDE.
    await factory_.UploadOkAsync(
      TestPlugins.Zip(
        TestPlugins.PluginXml(
          "com.example.future",
          "3.0",
          "261",
          name: "Future Support",
          extra: "<module value=\"com.example.modules.future\"/>\n"
        ),
        "future-support"
      ),
      "future-support-3.0.zip",
      "&tags=Languages"
    );
  }

  public async Task DisposeAsync() {
    client_.Dispose();
    await factory_.DisposeAsync();
  }

  [Fact]
  public async Task SearchReturnsMarketplaceShapedArray() {
    JsonElement result = await GetJsonAsync(
      $"/api/search/plugins?search=tool&build={C_BUILD_}&max=20&all=false&os=Linux&arch=X86_64"
    );
    JsonElement plugin = Assert.Single(result.EnumerateArray());
    Assert.Equal("com.example.tool", plugin.GetProperty("xmlId").GetString());
    Assert.Equal(JsonValueKind.Number, plugin.GetProperty("id").ValueKind);
    Assert.Equal(JsonValueKind.Number, plugin.GetProperty("updateId").ValueKind);
    Assert.Equal("Example Corp", plugin.GetProperty("organization").GetString());
    Assert.False(plugin.TryGetProperty("nearestUpdate", out _));
  }

  [Fact]
  public async Task SearchHidesIncompatiblePluginsUnlessAllIsRequested() {
    JsonElement listing = await GetJsonAsync(
      $"/api/search/plugins?orderBy=update+date&build={C_BUILD_}&max=10&all=false"
    );
    Assert.DoesNotContain(
      listing.EnumerateArray(),
      p => p.GetProperty("xmlId").GetString() == "com.example.future"
    );

    JsonElement all = await GetJsonAsync(
      $"/api/search/plugins?search=future&build={C_BUILD_}&max=10&all=true"
    );
    JsonElement future = Assert.Single(all.EnumerateArray());
    Assert.False(future.TryGetProperty("updateId", out _));
    Assert.False(
      future.GetProperty("nearestUpdate").GetProperty("isCompatible").GetBoolean()
    );

    JsonElement newer_ide = await GetJsonAsync(
      "/api/search/plugins?search=future&build=IU-261.1.1&max=10&all=false"
    );
    Assert.Single(newer_ide.EnumerateArray());
  }

  [Fact]
  public async Task SearchSupportsTagAndVendorFiltersAndAggregations() {
    JsonElement by_tag = await GetJsonAsync(
      "/api/search/plugins?tags=Languages&build=IU-261.1.1&max=10"
    );
    Assert.Equal(
      "com.example.future",
      Assert.Single(by_tag.EnumerateArray()).GetProperty("xmlId").GetString()
    );

    JsonElement by_vendor = await GetJsonAsync(
      $"/api/search/plugins?organization=Example%20Corp&build={C_BUILD_}&max=10"
    );
    Assert.Single(by_vendor.EnumerateArray());

    JsonElement tags = await GetJsonAsync(
      "/api/search/aggregation/tags?build=IU-261.1.1"
    );
    Assert.Equal(1, tags.GetProperty("aggregations").GetProperty("Languages").GetInt32());
    JsonElement vendors = await GetJsonAsync(
      $"/api/search/aggregation/organizations?build={C_BUILD_}"
    );
    Assert.True(
      vendors.GetProperty("aggregations").TryGetProperty("Example Corp", out _)
    );
  }

  [Fact]
  public async Task CompatibleUpdatesPickNewestCompatibleVersion() {
    // 2024.2+ IDEs: GET with repeated pluginXmlId parameters.
    JsonElement get = await GetJsonAsync(
      $"/api/search/updates/compatible?build={C_BUILD_}&os=Linux&arch=X86_64" +
      "&pluginXmlId=com.example.tool&pluginXmlId=com.example.future&pluginXmlId=unknown"
    );
    JsonElement update = Assert.Single(get.EnumerateArray());
    Assert.Equal("1.10.0", update.GetProperty("version").GetString());
    Assert.Equal("com.example.tool", update.GetProperty("pluginXmlId").GetString());

    // Older IDEs: POST with a JSON body.
    HttpResponseMessage post = await client_.PostAsync(
      "/api/search/compatibleUpdates",
      new StringContent(
        """{"build":"IU-243.21565.193","pluginXMLIds":["com.example.tool"]}""",
        Encoding.UTF8,
        "application/json"
      )
    );
    JsonElement old_ide = (await post.Content.ReadFromJsonAsync<JsonElement>());
    Assert.Equal(
      "1.0.0",
      Assert.Single(old_ide.EnumerateArray()).GetProperty("version").GetString()
    );
  }

  [Fact]
  public async Task CompatibleUpdateByModuleFindsProvider() {
    JsonElement result = await GetJsonAsync(
      "/api/search/updates/compatible?build=IU-261.1.1&module=com.example.modules.future"
    );
    Assert.Equal(
      "com.example.future",
      Assert.Single(result.EnumerateArray()).GetProperty("pluginXmlId").GetString()
    );
  }

  [Fact]
  public async Task UpdateMetadataMatchesIntellijUpdateMetadata() {
    JsonElement update = await GetUpdateAsync("com.example.tool");
    JsonElement meta = await GetJsonAsync(
      $"/files/{update.GetProperty("pluginId")}/{update.GetProperty("id")}/meta.json"
    );
    Assert.Equal("com.example.tool", meta.GetProperty("xmlId").GetString());
    Assert.Equal("com.example.tool Plugin", meta.GetProperty("name").GetString());
    Assert.Equal("1.10.0", meta.GetProperty("version").GetString());
    Assert.Equal("252", meta.GetProperty("since").GetString());
    Assert.Equal("252.*", meta.GetProperty("until").GetString());
    Assert.Equal("Example Corp", meta.GetProperty("vendor").GetString());
    Assert.Equal(JsonValueKind.Number, meta.GetProperty("size").ValueKind);
    Assert.Equal(
      ["com.intellij.modules.platform"],
      meta.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString())
    );

    JsonElement plugin_meta = await GetJsonAsync(
      $"/files/{update.GetProperty("pluginId")}/meta.json"
    );
    Assert.Equal(
      "Example Corp",
      plugin_meta.GetProperty("vendor").GetProperty("name").GetString()
    );
  }

  [Fact]
  public async Task DownloadRedirectsToArtifactWithCorrectExtension() {
    byte[] expected = TestPlugins.Jar(
      TestPlugins.PluginXml("com.example.tool", "1.10.0", "252", "252.*")
    );
    HttpResponseMessage redirect = await client_.GetAsync(
      $"/pluginManager?id=com.example.tool&build={C_BUILD_}&uuid=abc&updatedFrom=&os=Linux&arch=X86_64"
    );
    Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
    string location = redirect.Headers.Location!.OriginalString;
    // Relative, so it survives TLS-terminating proxies, and ends in .jar so the
    // IDE copies (not unzips) the plugin.
    Assert.StartsWith("/files/", location);
    Assert.EndsWith("/tool-1.10.0.jar", location);

    HttpResponseMessage file = await client_.GetAsync(location);
    Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    Assert.Equal(
      "tool-1.10.0.jar",
      file.Content.Headers.ContentDisposition!.FileName
    );
    Assert.Equal(expected.Length, (await file.Content.ReadAsByteArrayAsync()).Length);

    HttpResponseMessage zip = await client_.GetAsync(
      "/pluginManager/?action=download&id=com.example.future&build=IU-261.1"
    );
    Assert.EndsWith(".zip", zip.Headers.Location!.OriginalString);

    HttpResponseMessage blockmap = await client_.GetAsync(location + ".blockmap.zip");
    Assert.Equal(HttpStatusCode.NotFound, blockmap.StatusCode);

    HttpResponseMessage incompatible = await client_.GetAsync(
      $"/pluginManager?id=com.example.future&build={C_BUILD_}"
    );
    Assert.Equal(HttpStatusCode.NotFound, incompatible.StatusCode);
  }

  [Fact]
  public async Task DownloadsAreServedByteForByte() {
    // Mirrored Marketplace plugins carry a signature the IDE verifies, so the
    // bytes must be untouched.
    byte[] original = TestPlugins.Zip(TestPlugins.PluginXml("com.example.bytes", "1"));
    await factory_.UploadOkAsync(original, "bytes.zip");
    HttpResponseMessage redirect = await client_.GetAsync(
      "/pluginManager?id=com.example.bytes&build=IU-252.1"
    );
    byte[] served = await client_.GetByteArrayAsync(redirect.Headers.Location);
    Assert.Equal(original, served);
  }

  [Fact]
  public async Task PluginIdListSupportsConditionalRequests() {
    HttpResponseMessage first = await client_.GetAsync("/files/pluginsXMLIds.json");
    string[] ids = (await first.Content.ReadFromJsonAsync<string[]>())!;
    Assert.Equal(["com.example.future", "com.example.tool"], ids);

    HttpRequestMessage again = new(HttpMethod.Get, "/files/pluginsXMLIds.json");
    again.Headers.TryAddWithoutValidation(
      "If-None-Match",
      first.Headers.ETag!.ToString()
    );
    HttpResponseMessage second = await client_.SendAsync(again);
    Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
  }

  [Fact]
  public async Task ServesPluginIconsForBothThemes() {
    HttpResponseMessage light = await client_.GetAsync(
      "/api/icon?pluginId=com.example.tool"
    );
    Assert.Equal("image/svg+xml", light.Content.Headers.ContentType!.MediaType);
    Assert.Equal(TestPlugins.C_ICON, await light.Content.ReadAsStringAsync());
    HttpResponseMessage dark = await client_.GetAsync(
      "/api/icon?pluginId=com.example.tool&theme=DARCULA"
    );
    Assert.Equal(TestPlugins.C_DARK_ICON, await dark.Content.ReadAsStringAsync());
    HttpResponseMessage missing = await client_.GetAsync("/api/icon?pluginId=nope");
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
  }

  [Fact]
  public async Task BrokenPluginsIsOptionalAndUploadable() {
    // Absent: 404 makes the IDE keep its bundled list.
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await client_.GetAsync("/files/brokenPlugins.json")).StatusCode
    );
    HttpRequestMessage put = new(HttpMethod.Put, "/files/brokenPlugins.json") {
      Content = new StringContent(
        """[{"id":"a","version":"1","since":"0","until":"1"}]""",
        Encoding.UTF8,
        "application/json"
      )
    };
    put.Headers.Add("X-Api-Key", JetpackFactory.C_API_KEY);
    Assert.Equal(HttpStatusCode.NoContent, (await client_.SendAsync(put)).StatusCode);
    JsonElement list = await GetJsonAsync("/files/brokenPlugins.json");
    Assert.Equal(1, list.GetArrayLength());
  }

  [Fact]
  public async Task StubsReturnEmptyResults() {
    Assert.Equal(0, (await GetJsonAsync("/feature/getImplementations?featureType=x&implementationName=y")).GetArrayLength());
    Assert.Equal(0, (await GetJsonAsync("/api/products/intellij/plugins/com.example.tool/comments")).GetArrayLength());
    Assert.Equal(JsonValueKind.Object, (await GetJsonAsync($"/files/IDE/extensions.json?build={C_BUILD_}")).ValueKind);
  }

  private async Task<JsonElement> GetUpdateAsync(string plugin_id) {
    JsonElement updates = await GetJsonAsync(
      $"/api/search/updates/compatible?build={C_BUILD_}&pluginXmlId={plugin_id}"
    );
    return Assert.Single(updates.EnumerateArray());
  }

  private async Task<JsonElement> GetJsonAsync(string url) {
    HttpResponseMessage response = await client_.GetAsync(url);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{url} -> {response.StatusCode}: {body}");
    return JsonDocument.Parse(body).RootElement;
  }
}
