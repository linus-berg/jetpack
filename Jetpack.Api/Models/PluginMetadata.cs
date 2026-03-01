namespace Jetpack.Api.Models;

public class PluginMetadata {
  public string id { get; set; }
  public string name { get; set; }
  public string version { get; set; }
  public string description { get; set; }
  public string change_notes { get; set; }
  public string since_build { get; set; }
  public string until_build { get; set; }
  public string vendor { get; set; }
}