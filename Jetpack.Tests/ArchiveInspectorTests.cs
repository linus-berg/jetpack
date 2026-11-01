using Jetpack.Api.Plugins;

namespace Jetpack.Tests;

public sealed class ArchiveInspectorTests : IDisposable {
  private readonly List<string> files_ = new();

  public void Dispose() {
    foreach (string file in files_) {
      File.Delete(file);
    }
  }

  [Fact]
  public void DetectsJarPlugins() {
    InspectedPlugin plugin = Inspect(
      TestPlugins.Jar(
        TestPlugins.PluginXml(
          "com.example.jar",
          "1.2.3",
          "241",
          "252.*",
          extra: "<depends optional=\"true\" config-file=\"k.xml\">org.jetbrains.kotlin</depends>\n" +
                 "<module value=\"com.example.capability\"/>\n" +
                 "<category>Tools</category>\n"
        )
      )
    );
    Assert.Equal(PluginArchiveKind.JAR, plugin.Kind);
    Assert.Equal(".jar", plugin.Extension);
    Assert.Equal("com.example.jar", plugin.Descriptor.Id);
    Assert.Equal("1.2.3", plugin.Descriptor.Version);
    Assert.Equal("241", plugin.Descriptor.SinceBuild);
    Assert.Equal("252.*", plugin.Descriptor.UntilBuild);
    Assert.Equal("Example Corp", plugin.Descriptor.Vendor);
    Assert.Equal("https://example.com", plugin.Descriptor.VendorUrl);
    Assert.Equal(["com.intellij.modules.platform"], plugin.Descriptor.Dependencies);
    Assert.Equal(["org.jetbrains.kotlin"], plugin.Descriptor.OptionalDependencies);
    Assert.Equal(["com.example.capability"], plugin.Descriptor.Modules);
    Assert.Equal("Tools", plugin.Descriptor.Category);
    Assert.Contains("<b>useful</b>", plugin.Descriptor.Description);
    Assert.NotNull(plugin.Icon);
    Assert.NotNull(plugin.DarkIcon);
  }

  [Fact]
  public void DetectsZipPluginsAndFindsTheMainJar() {
    InspectedPlugin plugin = Inspect(
      TestPlugins.Zip(
        TestPlugins.PluginXml("com.example.zip", "2.0"),
        index_entry: true
      )
    );
    Assert.Equal(PluginArchiveKind.ZIP, plugin.Kind);
    Assert.Equal(".zip", plugin.Extension);
    Assert.Equal("com.example.zip", plugin.Descriptor.Id);
    Assert.NotNull(plugin.Icon);
  }

  [Fact]
  public void AcceptsDoctypeAndPluginModelV2Dependencies() {
    InspectedPlugin plugin = Inspect(
      TestPlugins.Jar(
        TestPlugins.PluginXml(
          "com.example.dtd",
          "1.0",
          doctype: true,
          extra: "<dependencies><plugin id=\"com.intellij.java\"/>" +
                 "<module name=\"intellij.platform.vcs\"/></dependencies>\n"
        )
      )
    );
    Assert.Equal(
      ["com.intellij.modules.platform", "com.intellij.java"],
      plugin.Descriptor.Dependencies
    );
  }

  [Fact]
  public void FallsBackToNameWhenIdIsMissing() {
    InspectedPlugin plugin = Inspect(
      TestPlugins.Jar(
        "<idea-plugin><name>Legacy Plugin</name><version>0.1</version></idea-plugin>"
      )
    );
    Assert.Equal("Legacy Plugin", plugin.Descriptor.Id);
    Assert.Null(plugin.Descriptor.SinceBuild);
  }

  [Theory]
  [InlineData("<idea-plugin><id>x</id></idea-plugin>", "no <version>")]
  [InlineData("<idea-plugin><version>1</version></idea-plugin>", "neither <id> nor <name>")]
  [InlineData("<plugin><id>x</id><version>1</version></plugin>", "expected <idea-plugin>")]
  [InlineData("<idea-plugin><id>x", "not valid XML")]
  public void RejectsBrokenDescriptors(string xml, string message) {
    InvalidPluginException ex = Assert.Throws<InvalidPluginException>(
      () => Inspect(TestPlugins.Jar(xml))
    );
    Assert.Contains(message, ex.Message);
  }

  [Fact]
  public void RejectsZipsWithoutSinglePluginDirectory() {
    byte[] jar = TestPlugins.Jar(TestPlugins.PluginXml("x", "1"));
    byte[] two_roots = TestPlugins.Archive(
      a => {
        TestPlugins.Add(a, "one/lib/one.jar", jar);
        TestPlugins.Add(a, "two/lib/two.jar", jar);
      }
    );
    Assert.Throws<InvalidPluginException>(() => Inspect(two_roots));

    byte[] flat = TestPlugins.Archive(a => TestPlugins.Add(a, "lib/x.jar", jar));
    Assert.Throws<InvalidPluginException>(() => Inspect(flat));

    byte[] no_descriptor = TestPlugins.Archive(
      a => TestPlugins.Add(
        a,
        "x/lib/x.jar",
        TestPlugins.Archive(b => TestPlugins.Add(b, "A.class", "a"))
      )
    );
    Assert.Contains(
      "none of the jars",
      Assert.Throws<InvalidPluginException>(() => Inspect(no_descriptor))
            .Message
    );
  }

  [Fact]
  public void RejectsNonArchives() {
    Assert.Contains(
      "not a zip or jar",
      Assert.Throws<InvalidPluginException>(
              () => Inspect("definitely not a zip"u8.ToArray())
            )
            .Message
    );
  }

  private InspectedPlugin Inspect(byte[] archive) {
    string path = Path.GetTempFileName();
    files_.Add(path);
    File.WriteAllBytes(path, archive);
    return PluginArchiveInspector.Inspect(path);
  }
}
