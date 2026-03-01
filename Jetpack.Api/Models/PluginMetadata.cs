namespace Jetpack.Api.Models;

/// <summary>
/// Represents the metadata of a plugin, extracted from the plugin.xml file.
/// </summary>
public class PluginMetadata {
  /// <summary>
  /// Gets or sets the unique identifier of the plugin.
  /// </summary>
  public string id { get; set; }

  /// <summary>
  /// Gets or sets the display name of the plugin.
  /// </summary>
  public string name { get; set; }

  /// <summary>
  /// Gets or sets the version of the plugin.
  /// </summary>
  public string version { get; set; }

  /// <summary>
  /// Gets or sets the description of the plugin.
  /// </summary>
  public string description { get; set; }

  /// <summary>
  /// Gets or sets the change notes for the plugin version.
  /// </summary>
  public string change_notes { get; set; }

  /// <summary>
  /// Gets or sets the minimum IDE build number required for this plugin.
  /// </summary>
  public string since_build { get; set; }

  /// <summary>
  /// Gets or sets the maximum IDE build number compatible with this plugin.
  /// </summary>
  public string until_build { get; set; }

  /// <summary>
  /// Gets or sets the vendor of the plugin.
  /// </summary>
  public string vendor { get; set; }
}
