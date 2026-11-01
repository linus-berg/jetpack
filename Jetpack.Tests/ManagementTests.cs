using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace Jetpack.Tests;

public sealed class ManagementTests : IAsyncLifetime {
  private readonly JetpackFactory factory_ = new();

  public Task InitializeAsync() {
    return Task.CompletedTask;
  }

  public async Task DisposeAsync() {
    await factory_.DisposeAsync();
  }

  [Fact]
  public async Task UploadsRequireTheApiKey() {
    byte[] jar = TestPlugins.Jar(TestPlugins.PluginXml("com.example.auth", "1"));
    Assert.Equal(
      HttpStatusCode.Unauthorized,
      (await factory_.UploadAsync(jar, "a.jar", api_key: null)).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Unauthorized,
      (await factory_.UploadAsync(jar, "a.jar", api_key: "")).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Unauthorized,
      (await factory_.UploadAsync(jar, "a.jar", api_key: "wrong")).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Created,
      (await factory_.UploadAsync(jar, "a.jar")).StatusCode
    );
  }

  [Fact]
  public async Task DuplicateVersionsConflictUnlessForced() {
    byte[] jar = TestPlugins.Jar(TestPlugins.PluginXml("com.example.dup", "1"));
    Assert.Equal(HttpStatusCode.Created, (await factory_.UploadAsync(jar, "d.jar")).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await factory_.UploadAsync(jar, "d.jar")).StatusCode);
    Assert.Equal(
      HttpStatusCode.OK,
      (await factory_.UploadAsync(jar, "renamed.jar", "&force=true")).StatusCode
    );
    JsonElement plugin = await GetPluginAsync("com.example.dup");
    JsonElement release = Assert.Single(plugin.GetProperty("releases").EnumerateArray());
    Assert.Equal("renamed.jar", release.GetProperty("fileName").GetString());
    Assert.False(File.Exists(ArtifactPath("com.example.dup", "1", "d.jar")));
    Assert.True(File.Exists(ArtifactPath("com.example.dup", "1", "renamed.jar")));
  }

  [Fact]
  public async Task FixesTheExtensionOfMislabelledUploads() {
    // The previous version stored jar plugins as .zip, which the IDE then
    // unpacked into a broken install.
    HttpResponseMessage response = await factory_.UploadAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.mislabelled", "1")),
      "mislabelled.zip"
    );
    JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("jar", body.GetProperty("plugin").GetProperty("kind").GetString());
    Assert.Equal(
      "mislabelled.jar",
      body.GetProperty("plugin").GetProperty("fileName").GetString()
    );
  }

  [Fact]
  public async Task RejectsInvalidUploadsWithAReason() {
    HttpResponseMessage response = await factory_.UploadAsync(
      "not a plugin"u8.ToArray(),
      "x.zip"
    );
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Contains("not a zip or jar", body.GetProperty("error").GetString());
  }

  [Fact]
  public async Task AcceptsMultipartUploads() {
    using HttpClient client = factory_.CreateRawClient();
    using MultipartFormDataContent form = new();
    form.Add(
      new ByteArrayContent(TestPlugins.Jar(TestPlugins.PluginXml("com.example.m1", "1"))),
      "files",
      "m1.jar"
    );
    form.Add(
      new ByteArrayContent(TestPlugins.Zip(TestPlugins.PluginXml("com.example.m2", "1"))),
      "files",
      "m2.zip"
    );
    form.Add(new ByteArrayContent("junk"u8.ToArray()), "files", "junk.zip");
    using HttpRequestMessage request = new(HttpMethod.Post, "/api/plugins/upload") {
      Content = form
    };
    request.Headers.Add("X-Api-Key", JetpackFactory.C_API_KEY);
    HttpResponseMessage response = await client.SendAsync(request);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(
      ["created", "created", "invalid"],
      body.GetProperty("results")
          .EnumerateArray()
          .Select(r => r.GetProperty("status").GetString())
    );
  }

  [Fact]
  public async Task BearerTokenIsAccepted() {
    using HttpClient client = factory_.CreateRawClient();
    using HttpRequestMessage request = new(
      HttpMethod.Post,
      "/api/plugins?filename=b.jar"
    ) {
      Content = new ByteArrayContent(
        TestPlugins.Jar(TestPlugins.PluginXml("com.example.bearer", "1"))
      )
    };
    request.Headers.Authorization = new AuthenticationHeaderValue(
      "Bearer",
      JetpackFactory.C_API_KEY
    );
    Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
  }

  [Fact]
  public async Task DeletesVersionsAndPlugins() {
    await factory_.UploadOkAsync(TestPlugins.Jar(TestPlugins.PluginXml("com.example.del", "1")), "1.jar");
    await factory_.UploadOkAsync(TestPlugins.Jar(TestPlugins.PluginXml("com.example.del", "2")), "2.jar");
    using HttpClient client = factory_.CreateRawClient();

    HttpRequestMessage unauthorized = new(HttpMethod.Delete, "/api/plugins/com.example.del/1");
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthorized)).StatusCode);

    Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(client, "/api/plugins/com.example.del/1")).StatusCode);
    JsonElement plugin = await GetPluginAsync("com.example.del");
    Assert.Equal(1, plugin.GetProperty("releases").GetArrayLength());
    Assert.False(Directory.Exists(Path.Combine(factory_.StoragePath, "plugins", "com.example.del", "1")));

    Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(client, "/api/plugins/com.example.del")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/plugins/com.example.del")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(client, "/api/plugins/com.example.del")).StatusCode);
  }

  [Fact]
  public async Task CatalogSurvivesRestartsAndKeepsIds() {
    JetpackFactory first = new() { DeleteStorageOnDispose = false };
    await first.UploadOkAsync(
      TestPlugins.Zip(TestPlugins.PluginXml("com.example.persist", "1.0")),
      "persist.zip"
    );
    using HttpClient first_client = first.CreateRawClient();
    string first_location = (await first_client.GetAsync(
                               "/pluginManager?id=com.example.persist&build=IU-252.1"
                             )).Headers.Location!.OriginalString;
    await first.DisposeAsync();

    await using JetpackFactory second = new(first.StoragePath);
    using HttpClient second_client = second.CreateRawClient();
    HttpResponseMessage redirect = await second_client.GetAsync(
      "/pluginManager?id=com.example.persist&build=IU-252.1"
    );
    Assert.Equal(first_location, redirect.Headers.Location!.OriginalString);
    Assert.Equal(
      HttpStatusCode.OK,
      (await second_client.GetAsync(redirect.Headers.Location)).StatusCode
    );
  }

  [Fact]
  public async Task CatalogPicksUpReleasesAddedToStorageByOthers() {
    await using JetpackFactory other = new(factory_.StoragePath) {
      DeleteStorageOnDispose = false
    };
    using HttpClient client = factory_.CreateRawClient();
    await client.GetAsync("/health");  // start this instance first
    await other.UploadOkAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.shared", "1")),
      "shared.jar"
    );
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/plugins/com.example.shared")).StatusCode);

    using HttpRequestMessage refresh = new(HttpMethod.Post, "/api/catalog/refresh");
    refresh.Headers.Add("X-Api-Key", JetpackFactory.C_API_KEY);
    Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(refresh)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/plugins/com.example.shared")).StatusCode);
  }

  [Fact]
  public async Task LegacyRepositoryXmlAndDownloadUrlsStillWork() {
    await factory_.UploadOkAsync(
      TestPlugins.Jar(
        TestPlugins.PluginXml(
          "com.example.legacy",
          "1.0",
          "241",
          "252.*",
          extra: "<depends optional=\"true\">optional.dep</depends>"
        )
      ),
      "legacy.jar"
    );
    using HttpClient client = factory_.CreateRawClient();
    string xml = await client.GetStringAsync("/api/plugins/updatePlugins.xml");
    XElement plugin = Assert.Single(XDocument.Parse(xml).Root!.Elements("plugin"));
    Assert.Equal("com.example.legacy", (string?)plugin.Attribute("id"));
    Assert.Equal("1.0", (string?)plugin.Attribute("version"));
    Assert.EndsWith("/legacy.jar", (string?)plugin.Attribute("url"));
    Assert.Equal("252.*", (string?)plugin.Element("idea-version")!.Attribute("until-build"));
    Assert.Contains("<b>useful</b>", plugin.Element("description")!.Value);
    // Only required dependencies: the IDE treats every <depends> as mandatory.
    Assert.Equal(["com.intellij.modules.platform"], plugin.Elements("depends").Select(d => d.Value));

    string filtered = await client.GetStringAsync("/updatePlugins.xml?build=IU-261.1");
    Assert.Empty(XDocument.Parse(filtered).Root!.Elements("plugin"));

    HttpResponseMessage old_url = await client.GetAsync(
      "/api/plugins/download/com.example.legacy-1.0.zip"
    );
    Assert.Equal(HttpStatusCode.Redirect, old_url.StatusCode);
    Assert.EndsWith("/legacy.jar", old_url.Headers.Location!.OriginalString);
  }

  [Fact]
  public async Task CDataTerminatorsInDescriptionsDoNotBreakTheXml() {
    await factory_.UploadOkAsync(
      TestPlugins.Jar(
        "<idea-plugin><id>com.example.cdata</id><version>1</version>" +
        "<description>a ]]&gt; b</description></idea-plugin>"
      ),
      "cdata.jar"
    );
    using HttpClient client = factory_.CreateRawClient();
    string xml = await client.GetStringAsync("/updatePlugins.xml");
    Assert.Equal(
      "a ]]> b",
      XDocument.Parse(xml).Root!.Element("plugin")!.Element("description")!.Value
    );
  }

  [Fact]
  public async Task WebPagesRender() {
    await factory_.UploadOkAsync(
      TestPlugins.Jar(TestPlugins.PluginXml("com.example.web", "1", name: "<script>x</script>")),
      "web.jar"
    );
    using HttpClient client = factory_.CreateRawClient();
    string index = await client.GetStringAsync("/");
    Assert.Contains("com.example.web", index);
    Assert.DoesNotContain("<script>x</script>", index);
    HttpResponseMessage page = await client.GetAsync("/plugin/index?xmlId=com.example.web");
    Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/plugin/index?xmlId=nope")).StatusCode);
  }

  [Fact]
  public async Task HealthReportsStorage() {
    using HttpClient client = factory_.CreateRawClient();
    JsonElement health = await client.GetFromJsonAsync<JsonElement>("/health");
    Assert.Equal("ok", health.GetProperty("status").GetString());
    Assert.Contains("file system", health.GetProperty("storage").GetString());
  }

  private async Task<JsonElement> GetPluginAsync(string id) {
    using HttpClient client = factory_.CreateRawClient();
    return await client.GetFromJsonAsync<JsonElement>($"/api/plugins/{id}");
  }

  private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client,
                                                             string url) {
    using HttpRequestMessage request = new(HttpMethod.Delete, url);
    request.Headers.Add("X-Api-Key", JetpackFactory.C_API_KEY);
    return await client.SendAsync(request);
  }

  private string ArtifactPath(string id, string version, string file) {
    return Path.Combine(factory_.StoragePath, "plugins", id, version, file);
  }
}
