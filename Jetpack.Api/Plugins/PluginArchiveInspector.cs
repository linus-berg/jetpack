using System.IO.Compression;

namespace Jetpack.Api.Plugins;

public enum PluginArchiveKind {
  /// <summary>A single jar with <c>META-INF/plugin.xml</c> at its root.</summary>
  JAR,

  /// <summary>A zip with one top-level directory containing <c>lib/*.jar</c>.</summary>
  ZIP
}

public sealed class InspectedPlugin {
  public required PluginDescriptor Descriptor { get; init; }
  public required PluginArchiveKind Kind { get; init; }
  public byte[]? Icon { get; init; }
  public byte[]? DarkIcon { get; init; }

  public string Extension => Kind == PluginArchiveKind.JAR ? ".jar" : ".zip";
}

/// <summary>
///   Validates an uploaded plugin archive the same way the IDE will load it and
///   extracts its descriptor and icons.
///   The layout matters: the IDE copies <c>.jar</c> files into its plugins
///   directory and unpacks <c>.zip</c> files, so a jar plugin must never be
///   served as a zip (or vice versa).
/// </summary>
public static class PluginArchiveInspector {
  private const string C_PLUGIN_XML_ = "META-INF/plugin.xml";
  private const string C_ICON_ = "META-INF/pluginIcon.svg";
  private const string C_DARK_ICON_ = "META-INF/pluginIcon_dark.svg";
  private const long C_MAX_PLUGIN_XML_BYTES_ = 32L * 1024 * 1024;
  private const long C_MAX_ICON_BYTES_ = 2L * 1024 * 1024;

  public static InspectedPlugin Inspect(string archive_path) {
    using FileStream stream = File.OpenRead(archive_path);
    using ZipArchive archive = OpenArchive(stream, "The upload");

    // 1. Single-jar plugin.
    ZipArchiveEntry? plugin_xml = archive.GetEntry(C_PLUGIN_XML_);
    if (plugin_xml != null) {
      return new InspectedPlugin {
        Descriptor = ReadDescriptor(plugin_xml),
        Kind = PluginArchiveKind.JAR,
        Icon = ReadIcon(archive, C_ICON_),
        DarkIcon = ReadIcon(archive, C_DARK_ICON_)
      };
    }

    // 2. Zip distribution: <plugin-dir>/lib/*.jar
    // The IDE unpacks the zip and loads the first top-level entry (ignoring the
    // "__index__" file IntelliJ build tooling adds), so exactly one top-level
    // directory is required.
    List<string> roots = archive.Entries
                                .Select(e => e.FullName.Replace('\\', '/'))
                                .Where(n => !n.StartsWith("__MACOSX/"))
                                .Select(n => n.Split('/')[0])
                                .Where(n => n.Length > 0 && n != "__index__")
                                .Distinct(StringComparer.Ordinal)
                                .ToList();
    bool has_top_level_files = archive.Entries.Any(
      e => e.FullName.Length > 0 &&
           e.FullName != "__index__" &&
           !e.FullName.Replace('\\', '/').Contains('/')
    );
    if (roots.Count != 1 || has_top_level_files) {
      throw new InvalidPluginException(
        "Not a valid plugin: expected either a jar with META-INF/plugin.xml or " +
        "a zip with a single top-level directory containing lib/*.jar " +
        $"(found top-level entries: {string.Join(", ", roots.Take(5))})."
      );
    }

    string root = roots[0];
    string lib_prefix = $"{root}/lib/";
    List<ZipArchiveEntry> jars = archive.Entries
                                        .Where(
                                          e => IsDirectChild(
                                            e.FullName.Replace('\\', '/'),
                                            lib_prefix
                                          )
                                        )
                                        .OrderBy(e => JarPriority(e.Name, root))
                                        .ThenBy(
                                          e => e.Name,
                                          StringComparer.Ordinal
                                        )
                                        .ToList();
    if (jars.Count == 0) {
      throw new InvalidPluginException(
        $"Not a valid plugin: the zip contains no {lib_prefix}*.jar files."
      );
    }

    foreach (ZipArchiveEntry jar in jars) {
      InspectedPlugin? inspected = TryInspectNestedJar(jar);
      if (inspected != null) {
        return inspected;
      }
    }

    throw new InvalidPluginException(
      $"Not a valid plugin: none of the jars in {lib_prefix} contains " +
      $"{C_PLUGIN_XML_}."
    );
  }

  private static InspectedPlugin? TryInspectNestedJar(ZipArchiveEntry jar) {
    // ZipArchive needs a seekable stream, so spool the jar to disk.
    string temp_path = Path.Combine(
      Path.GetTempPath(),
      $"jetpack-jar-{Guid.NewGuid():N}.jar"
    );
    try {
      using (Stream input = jar.Open())
      using (FileStream output = File.Create(temp_path)) {
        input.CopyTo(output);
      }

      using FileStream stream = File.OpenRead(temp_path);
      ZipArchive nested;
      try {
        nested = new ZipArchive(stream, ZipArchiveMode.Read);
      } catch (InvalidDataException) {
        return null;
      }

      using (nested) {
        ZipArchiveEntry? plugin_xml = nested.GetEntry(C_PLUGIN_XML_);
        if (plugin_xml == null) {
          return null;
        }

        return new InspectedPlugin {
          Descriptor = ReadDescriptor(plugin_xml),
          Kind = PluginArchiveKind.ZIP,
          Icon = ReadIcon(nested, C_ICON_),
          DarkIcon = ReadIcon(nested, C_DARK_ICON_)
        };
      }
    } finally {
      try {
        File.Delete(temp_path);
      } catch (IOException) {
        // Best effort.
      }
    }
  }

  private static ZipArchive OpenArchive(Stream stream, string what) {
    try {
      return new ZipArchive(stream, ZipArchiveMode.Read);
    } catch (InvalidDataException) {
      throw new InvalidPluginException(
        $"{what} is not a zip or jar archive."
      );
    }
  }

  private static PluginDescriptor ReadDescriptor(ZipArchiveEntry entry) {
    if (entry.Length > C_MAX_PLUGIN_XML_BYTES_) {
      throw new InvalidPluginException("META-INF/plugin.xml is too large.");
    }

    using Stream stream = entry.Open();
    return PluginDescriptorParser.Parse(stream);
  }

  private static byte[]? ReadIcon(ZipArchive archive, string name) {
    ZipArchiveEntry? entry = archive.GetEntry(name);
    if (entry == null ||
        entry.Length == 0 ||
        entry.Length > C_MAX_ICON_BYTES_) {
      return null;
    }

    using Stream stream = entry.Open();
    using MemoryStream buffer = new();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static bool IsDirectChild(string name, string prefix) {
    return name.StartsWith(prefix, StringComparison.Ordinal) &&
           name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
           name.IndexOf('/', prefix.Length) < 0;
  }

  /// <summary>
  ///   Like the IDE, try the jar named after the plugin directory first, then
  ///   unversioned jars, and third-party (versioned) jars last.
  /// </summary>
  private static int JarPriority(string jar_name, string root) {
    string base_name = Path.GetFileNameWithoutExtension(jar_name);
    if (string.Equals(base_name, root, StringComparison.OrdinalIgnoreCase)) {
      return 0;
    }

    if (base_name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
      return 1;
    }

    return base_name.Contains('-') ? 3 : 2;
  }
}
