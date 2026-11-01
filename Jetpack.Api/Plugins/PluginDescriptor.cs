using System.Xml;
using System.Xml.Linq;

namespace Jetpack.Api.Plugins;

/// <summary>The parts of a plugin's <c>META-INF/plugin.xml</c> Jetpack needs.</summary>
public sealed class PluginDescriptor {
  public required string Id { get; init; }
  public required string Name { get; init; }
  public required string Version { get; init; }
  public string? Vendor { get; init; }
  public string? VendorUrl { get; init; }
  public string? VendorEmail { get; init; }
  public string? Description { get; init; }
  public string? ChangeNotes { get; init; }
  public string? SinceBuild { get; init; }
  public string? UntilBuild { get; init; }
  public string? Url { get; init; }
  public string? Category { get; init; }
  public string? ProductCode { get; init; }
  public IReadOnlyList<string> Dependencies { get; init; } = [];
  public IReadOnlyList<string> OptionalDependencies { get; init; } = [];

  /// <summary>Module aliases declared with <c>&lt;module value="..."/&gt;</c>.</summary>
  public IReadOnlyList<string> Modules { get; init; } = [];
}

public sealed class InvalidPluginException(string message) : Exception(message);

public static class PluginDescriptorParser {
  private const int C_MAX_ID_LENGTH_ = 255;
  private const int C_MAX_VERSION_LENGTH_ = 128;

  public static PluginDescriptor Parse(Stream plugin_xml) {
    XDocument document;
    try {
      // Many plugin.xml files carry a <!DOCTYPE>; ignore it instead of failing
      // and never resolve external entities.
      XmlReaderSettings settings = new() {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersInDocument = 64L * 1024 * 1024
      };
      using XmlReader reader = XmlReader.Create(plugin_xml, settings);
      document = XDocument.Load(reader);
    } catch (XmlException ex) {
      throw new InvalidPluginException(
        $"META-INF/plugin.xml is not valid XML: {ex.Message}"
      );
    }

    XElement root = document.Root ??
                    throw new InvalidPluginException(
                      "META-INF/plugin.xml is empty."
                    );
    if (root.Name.LocalName != "idea-plugin") {
      throw new InvalidPluginException(
        $"META-INF/plugin.xml has root <{root.Name.LocalName}>, " +
        "expected <idea-plugin>."
      );
    }

    string? name = Text(root.Element("name"));
    string id = Text(root.Element("id")) ??
                name ??
                throw new InvalidPluginException(
                  "plugin.xml declares neither <id> nor <name>."
                );
    string version = Text(root.Element("version")) ??
                     throw new InvalidPluginException(
                       $"plugin.xml of '{id}' has no <version>."
                     );
    Validate(id, "id", C_MAX_ID_LENGTH_);
    Validate(version, "version", C_MAX_VERSION_LENGTH_);

    List<string> dependencies = new();
    List<string> optional_dependencies = new();
    foreach (XElement depends in root.Elements("depends")) {
      string? dependency = Text(depends);
      if (dependency == null) {
        continue;
      }

      bool optional = string.Equals(
        (string?)depends.Attribute("optional"),
        "true",
        StringComparison.OrdinalIgnoreCase
      );
      (optional ? optional_dependencies : dependencies).Add(dependency);
    }

    // Plugin model v2: <dependencies><plugin id="..."/></dependencies>
    foreach (XElement plugin in root.Elements("dependencies")
                                    .Elements("plugin")) {
      string? dependency = Attribute(plugin, "id");
      if (dependency != null) {
        dependencies.Add(dependency);
      }
    }

    XElement? vendor = root.Element("vendor");
    XElement? idea_version = root.Element("idea-version");
    return new PluginDescriptor {
      Id = id,
      Name = name ?? id,
      Version = version,
      Vendor = Text(vendor),
      VendorUrl = Attribute(vendor, "url"),
      VendorEmail = Attribute(vendor, "email"),
      Description = Text(root.Element("description")),
      ChangeNotes = Text(root.Element("change-notes")),
      SinceBuild = Attribute(idea_version, "since-build"),
      UntilBuild = Attribute(idea_version, "until-build"),
      Url = Attribute(root, "url"),
      Category = Text(root.Element("category")),
      ProductCode = Attribute(root.Element("product-descriptor"), "code"),
      Dependencies = dependencies.Distinct(StringComparer.Ordinal).ToList(),
      OptionalDependencies = optional_dependencies
                             .Except(dependencies, StringComparer.Ordinal)
                             .Distinct(StringComparer.Ordinal)
                             .ToList(),
      Modules = root.Elements("module")
                    .Select(m => Attribute(m, "value"))
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
    };
  }

  private static void Validate(string value, string what, int max_length) {
    if (value.Length > max_length) {
      throw new InvalidPluginException(
        $"Plugin {what} is longer than {max_length} characters."
      );
    }

    if (value.Any(char.IsControl)) {
      throw new InvalidPluginException(
        $"Plugin {what} contains control characters."
      );
    }
  }

  private static string? Text(XElement? element) {
    string? value = element?.Value.Trim();
    return string.IsNullOrEmpty(value) ? null : value;
  }

  private static string? Attribute(XElement? element, string name) {
    string? value = element?.Attribute(name)?.Value.Trim();
    return string.IsNullOrEmpty(value) ? null : value;
  }
}
