using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Jetpack.Api.Compatibility;

namespace Jetpack.Tests;

public class PlatformRequirementTests {
  [Theory]
  [InlineData("Linux 6.19.10-300.fc44.x86_64", HostOs.LINUX)]
  [InlineData("Windows 11", HostOs.WINDOWS)]
  [InlineData("macOS 15.1", HostOs.MAC)]
  [InlineData("Mac OS X", HostOs.MAC)]
  [InlineData("FreeBSD 14", HostOs.FREE_BSD)]
  [InlineData("OHOS", HostOs.OHOS)]
  [InlineData("Plan 9", HostOs.OTHER)]
  public void ParsesTheOsParameterLikeTheIde(string os, HostOs expected) {
    Assert.Equal(expected, ClientPlatform.ParseOs(os));
  }

  [Theory]
  [InlineData("X86_64", HostArch.X86_64)]
  [InlineData("amd64", HostArch.X86_64)]
  [InlineData("ARM64", HostArch.ARM64)]
  [InlineData("aarch64", HostArch.ARM64)]
  [InlineData("X86", HostArch.X86)]
  [InlineData("ARM32", HostArch.ARM32)]
  [InlineData("riscv64", HostArch.OTHER)]
  public void ParsesTheArchParameterLikeTheIde(string arch, HostArch expected) {
    Assert.Equal(expected, ClientPlatform.ParseArch(arch));
  }

  [Fact]
  public void MissingPlatformIsUnknown() {
    Assert.False(ClientPlatform.Parse(null, " ").IsKnown);
  }

  [Theory]
  [InlineData("linux", "x86_64", "Linux", "X86_64", true)]
  [InlineData("linux", "x86_64", "Linux", "ARM64", false)]
  [InlineData("linux", "x86_64", "Windows 11", "X86_64", false)]
  [InlineData("mac", "arm64", "macOS 15", "ARM64", true)]
  [InlineData("unix", null, "Linux", "X86_64", true)]
  [InlineData("unix", null, "macOS", "ARM64", true)]
  [InlineData("unix", null, "Windows 11", "X86_64", false)]
  [InlineData("xwindow", null, "Linux", "X86_64", true)]
  [InlineData("xwindow", null, "macOS", "ARM64", false)]
  [InlineData("solaris", null, "Linux", "X86_64", false)]
  [InlineData(null, "riscv", "Linux", "X86_64", false)]
  [InlineData("windows", "x86_64", null, null, null)]
  [InlineData("windows", null, null, "X86_64", null)]
  public void EvaluatesRequirementsLikeTheIde(string? os_module,
                                              string? arch_module,
                                              string? os, string? arch,
                                              bool? expected) {
    List<string> dependencies = ["com.intellij.modules.platform"];
    if (os_module != null) {
      dependencies.Add($"com.intellij.modules.os.{os_module}");
    }

    if (arch_module != null) {
      dependencies.Add($"com.intellij.modules.arch.{arch_module}");
    }

    PlatformRequirement requirement =
      PlatformRequirement.FromRelease(dependencies, "1.0");
    Assert.True(requirement.IsPlatformSpecific);
    Assert.Equal(expected, requirement.Evaluate(ClientPlatform.Parse(os, arch)));
  }

  [Fact]
  public void OrdinaryPluginsFitEverywhere() {
    PlatformRequirement requirement = PlatformRequirement.FromRelease(
      ["com.intellij.modules.platform", "com.intellij.modules.lang"],
      "1.0-linux-arm64"
    );
    Assert.False(requirement.IsPlatformSpecific);
    Assert.Null(requirement.Label);
    Assert.True(requirement.Evaluate(ClientPlatform.S_UNKNOWN));
    Assert.True(requirement.Evaluate(ClientPlatform.Parse("Windows 11", "X86")));
  }

  [Fact]
  public void InfersPlatformFromVersionOnlyWithoutDependencies() {
    // PluginCompatibilityUtils: "version-os-arch" when there are no dependencies.
    PlatformRequirement requirement =
      PlatformRequirement.FromRelease([], "1.0-linux-aarch64");
    Assert.Equal("linux/arm64", requirement.Label);
    Assert.True(requirement.Evaluate(ClientPlatform.Parse("Linux", "ARM64")));
    Assert.False(requirement.Evaluate(ClientPlatform.Parse("Linux", "X86_64")));
    Assert.False(requirement.Evaluate(ClientPlatform.Parse("macOS", "ARM64")));

    Assert.False(PlatformRequirement.FromRelease([], "1.0.3").IsPlatformSpecific);
    Assert.False(PlatformRequirement.FromRelease([], "1.0-beta-2").IsPlatformSpecific);
  }

  [Fact]
  public void LabelsDescribeTheVariant() {
    Assert.Equal(
      "linux/x86_64",
      PlatformRequirement.FromRelease(
                           [
                             "com.intellij.modules.os.linux",
                             "com.intellij.modules.arch.x86_64"
                           ],
                           "1"
                         )
                         .Label
    );
    Assert.Equal(
      "mac/any",
      PlatformRequirement.FromRelease(["com.intellij.modules.os.mac"], "1").Label
    );
  }
}

/// <summary>
///   Platform-specific builds as published by e.g. GitHub Copilot: one
///   platform-independent version for older IDEs and one version per OS/CPU
///   for newer ones, distinguished by required os/arch modules.
/// </summary>
public sealed class PlatformSelectionTests : IAsyncLifetime {
  private const string C_ID_ = "com.example.native";
  private const string C_NEW_IDE_ = "IU-261.20000.1";
  private const string C_LINUX_X64_ = "os=Linux%206.19.10&arch=X86_64";
  private const string C_WINDOWS_X64_ = "os=Windows%2011&arch=X86_64";
  private const string C_MAC_ARM64_ = "os=macOS%2015.1&arch=ARM64";
  private const string C_LINUX_ARM64_ = "os=Linux%206.19.10&arch=ARM64";
  private readonly JetpackFactory factory_ = new();
  private HttpClient client_ = null!;

  public async Task InitializeAsync() {
    client_ = factory_.CreateRawClient();
    await UploadAsync("1.20.0-252", "252", "253.*", null, null);
    await UploadAsync("1.20.0-261-linux-x64", "261", null, "linux", "x86_64");
    await UploadAsync("1.20.0-261-windows-x64", "261", null, "windows", "x86_64");
    await UploadAsync("1.20.0-261-macos-arm64", "261", null, "mac", "arm64");
  }

  public async Task DisposeAsync() {
    client_.Dispose();
    await factory_.DisposeAsync();
  }

  [Theory]
  [InlineData(C_LINUX_X64_, "1.20.0-261-linux-x64")]
  [InlineData(C_WINDOWS_X64_, "1.20.0-261-windows-x64")]
  [InlineData(C_MAC_ARM64_, "1.20.0-261-macos-arm64")]
  public async Task DownloadServesTheVariantForTheIdesPlatform(string platform,
    string version) {
    HttpResponseMessage redirect = await client_.GetAsync(
      $"/pluginManager?id={C_ID_}&build={C_NEW_IDE_}&uuid=x&updatedFrom=&{platform}"
    );
    Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
    Assert.EndsWith($"/native-{version}.zip", redirect.Headers.Location!.OriginalString);
  }

  [Fact]
  public async Task NoVariantForThePlatformMeansNotFound() {
    HttpResponseMessage response = await client_.GetAsync(
      $"/pluginManager?id={C_ID_}&build={C_NEW_IDE_}&{C_LINUX_ARM64_}"
    );
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    JsonElement search = await GetJsonAsync(
      $"/api/search/plugins?search=native&build={C_NEW_IDE_}&max=10&all=false&{C_LINUX_ARM64_}"
    );
    Assert.Equal(0, search.GetArrayLength());
  }

  [Fact]
  public async Task OlderIdesGetThePlatformIndependentVersion() {
    // 2025.3 sends its platform but only the -252 build supports it.
    Assert.Equal("1.20.0-252", await CompatibleVersionAsync("IU-253.28294.334", C_MAC_ARM64_));
    // 2025.2 and older send no platform at all.
    Assert.Equal("1.20.0-252", await CompatibleVersionAsync("IU-252.25557.131", ""));
  }

  [Theory]
  [InlineData(C_LINUX_X64_, "1.20.0-261-linux-x64")]
  [InlineData(C_WINDOWS_X64_, "1.20.0-261-windows-x64")]
  public async Task UpdateChecksAreSentTheMatchingVariant(string platform,
                                                          string version) {
    Assert.Equal(version, await CompatibleVersionAsync(C_NEW_IDE_, platform));

    // IDEs that POST put os/arch in the body (OS / CpuArch enum names).
    string os = platform.Contains("Windows") ? "Windows" : "Linux";
    HttpResponseMessage post = await client_.PostAsync(
      "/api/search/compatibleUpdates",
      new StringContent(
        $$"""{"build":"{{C_NEW_IDE_}}","pluginXMLIds":["{{C_ID_}}"],"os":"{{os}}","arch":"X86_64"}""",
        Encoding.UTF8,
        "application/json"
      )
    );
    JsonElement updates = await post.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(
      version,
      Assert.Single(updates.EnumerateArray()).GetProperty("version").GetString()
    );
  }

  [Fact]
  public async Task SearchLinksTheVariantForTheIdesPlatform() {
    JsonElement mac = Assert.Single(
      (await GetJsonAsync(
        $"/api/search/plugins?search=native&build={C_NEW_IDE_}&max=10&all=false&{C_MAC_ARM64_}"
      )).EnumerateArray()
    );
    JsonElement meta = await GetJsonAsync(
      $"/files/{mac.GetProperty("id")}/{mac.GetProperty("updateId")}/meta.json"
    );
    Assert.Equal("1.20.0-261-macos-arm64", meta.GetProperty("version").GetString());
    Assert.Contains(
      "com.intellij.modules.os.mac",
      meta.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString())
    );
  }

  [Fact]
  public async Task ClientsWithoutPlatformStillGetAVariant() {
    // Like plugins.jetbrains.com: some variant rather than nothing.
    string? version = await CompatibleVersionAsync(C_NEW_IDE_, "");
    Assert.StartsWith("1.20.0-261-", version);
  }

  [Fact]
  public async Task LegacyRepositoryXmlIsPlatformAware() {
    XElement linux = XDocument.Parse(
                                await client_.GetStringAsync(
                                  $"/updatePlugins.xml?build={C_NEW_IDE_}&{C_LINUX_X64_}"
                                )
                              )
                              .Root!;
    Assert.Equal(
      ["1.20.0-261-linux-x64"],
      linux.Elements("plugin").Select(p => (string?)p.Attribute("version"))
    );
    Assert.Contains(
      "com.intellij.modules.os.linux",
      linux.Element("plugin")!.Elements("depends").Select(d => d.Value)
    );

    // Without a platform every variant is listed; the IDE filters by <depends>.
    XElement unknown = XDocument.Parse(
                                  await client_.GetStringAsync(
                                    $"/updatePlugins.xml?build={C_NEW_IDE_}"
                                  )
                                )
                                .Root!;
    Assert.Equal(
      [
        "1.20.0-261-linux-x64",
        "1.20.0-261-macos-arm64",
        "1.20.0-261-windows-x64"
      ],
      unknown.Elements("plugin")
             .Select(p => (string)p.Attribute("version")!)
             .Order(StringComparer.Ordinal)
    );
  }

  [Fact]
  public async Task ManagementApiShowsThePlatform() {
    JsonElement plugin = await GetJsonAsync($"/api/plugins/{C_ID_}");
    Dictionary<string, string?> platforms = plugin.GetProperty("releases")
                                                  .EnumerateArray()
                                                  .ToDictionary(
                                                    r => r.GetProperty("version").GetString()!,
                                                    r => r.TryGetProperty("platform", out JsonElement p)
                                                           ? p.GetString()
                                                           : null
                                                  );
    Assert.Null(platforms["1.20.0-252"]);
    Assert.Equal("linux/x86_64", platforms["1.20.0-261-linux-x64"]);
    Assert.Equal("mac/arm64", platforms["1.20.0-261-macos-arm64"]);
  }

  private async Task<string?> CompatibleVersionAsync(string build,
                                                     string platform) {
    JsonElement updates = await GetJsonAsync(
      $"/api/search/updates/compatible?build={build}&pluginXmlId={C_ID_}&{platform}"
    );
    return updates.GetArrayLength() == 0
             ? null
             : updates[0].GetProperty("version").GetString();
  }

  private async Task UploadAsync(string version, string since, string? until,
                                 string? os, string? arch) {
    StringBuilder extra = new();
    if (os != null) {
      extra.Append($"<depends>com.intellij.modules.os.{os}</depends>\n");
    }

    if (arch != null) {
      extra.Append($"<depends>com.intellij.modules.arch.{arch}</depends>\n");
    }

    await factory_.UploadOkAsync(
      TestPlugins.Zip(
        TestPlugins.PluginXml(C_ID_, version, since, until, "Native Helper",
                              extra.ToString()),
        "native"
      ),
      $"native-{version}.zip"
    );
  }

  private async Task<JsonElement> GetJsonAsync(string url) {
    HttpResponseMessage response = await client_.GetAsync(url);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{url} -> {response.StatusCode}: {body}");
    return JsonDocument.Parse(body).RootElement;
  }
}
