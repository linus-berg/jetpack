using System.IO.Compression;
using System.Text;

namespace Jetpack.Tests;

/// <summary>Builds synthetic plugin archives in both distribution formats.</summary>
public static class TestPlugins {
  public const string C_ICON =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"/>";

  public const string C_DARK_ICON =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"><g/></svg>";

  public static string PluginXml(string id, string version,
                                 string? since = "241",
                                 string? until = null,
                                 string? name = null,
                                 string extra = "",
                                 bool doctype = false) {
    StringBuilder xml = new();
    if (doctype) {
      xml.Append(
        "<!DOCTYPE idea-plugin PUBLIC \"Plugin/DTD\" " +
        "\"https://plugins.jetbrains.com/plugin.dtd\">\n"
      );
    }

    xml.Append("<idea-plugin>\n");
    xml.Append($"  <id>{id}</id>\n");
    xml.Append($"  <name>{name ?? id + " Plugin"}</name>\n");
    xml.Append($"  <version>{version}</version>\n");
    xml.Append("  <vendor url=\"https://example.com\">Example Corp</vendor>\n");
    xml.Append(
      "  <description><![CDATA[<p>Adds <b>useful</b> features for testing.</p>]]></description>\n"
    );
    xml.Append($"  <change-notes><![CDATA[Version {version}]]></change-notes>\n");
    xml.Append("  <idea-version");
    if (since != null) {
      xml.Append($" since-build=\"{since}\"");
    }

    if (until != null) {
      xml.Append($" until-build=\"{until}\"");
    }

    xml.Append("/>\n");
    xml.Append("  <depends>com.intellij.modules.platform</depends>\n");
    xml.Append(extra);
    xml.Append("</idea-plugin>\n");
    return xml.ToString();
  }

  /// <summary>A single-jar plugin (META-INF/plugin.xml at the root).</summary>
  public static byte[] Jar(string plugin_xml, bool icons = true) {
    return Archive(
      archive => {
        Add(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n");
        Add(archive, "META-INF/plugin.xml", plugin_xml);
        if (icons) {
          Add(archive, "META-INF/pluginIcon.svg", C_ICON);
          Add(archive, "META-INF/pluginIcon_dark.svg", C_DARK_ICON);
        }

        Add(archive, "com/example/Plugin.class", "not really a class");
      }
    );
  }

  /// <summary>A zip distribution: {dir}/lib/{dir}.jar plus a third-party jar.</summary>
  public static byte[] Zip(string plugin_xml, string directory = "example-plugin",
                           bool index_entry = false) {
    byte[] main_jar = Jar(plugin_xml);
    byte[] library_jar = Archive(
      archive => Add(archive, "org/library/Library.class", "library")
    );
    return Archive(
      archive => {
        if (index_entry) {
          Add(archive, "__index__", "index");
        }

        Add(archive, $"{directory}/lib/library-1.2.3.jar", library_jar);
        Add(archive, $"{directory}/lib/{directory}.jar", main_jar);
      }
    );
  }

  public static byte[] Archive(Action<ZipArchive> fill) {
    using MemoryStream stream = new();
    using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true)) {
      fill(archive);
    }

    return stream.ToArray();
  }

  public static void Add(ZipArchive archive, string name, string content) {
    Add(archive, name, Encoding.UTF8.GetBytes(content));
  }

  public static void Add(ZipArchive archive, string name, byte[] content) {
    using Stream entry = archive.CreateEntry(name).Open();
    entry.Write(content);
  }
}
