using System.Text.Json.Serialization;
using Jetpack.Api.Compatibility;

namespace Jetpack.Api.Endpoints;

// Wire formats of the JetBrains Marketplace API as consumed by the IDE
// (com.intellij.ide.plugins.marketplace.PluginRepositoryObjects). Field names
// and JSON types match what plugins.jetbrains.com returns.

/// <summary>Item of <c>GET /api/search/plugins</c>.</summary>
public sealed record SearchPluginItem(
  [property: JsonPropertyName("id")] int Id,
  [property: JsonPropertyName("xmlId")] string XmlId,
  [property: JsonPropertyName("name")] string Name,
  [property: JsonPropertyName("preview")]
  string? Preview,
  [property: JsonPropertyName("paid")] bool Paid,
  [property: JsonPropertyName("downloads")]
  long Downloads,
  [property: JsonPropertyName("organization")]
  string? Organization,
  [property: JsonPropertyName("cdate")] long CreatedAt,
  [property: JsonPropertyName("updateId")]
  int? UpdateId,
  [property: JsonPropertyName("icon")] string? Icon,
  [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
  [property: JsonPropertyName("vendorInfo")]
  VendorInfo? VendorInfo,
  [property: JsonPropertyName("nearestUpdate")]
  NearestUpdate? NearestUpdate
);

public sealed record VendorInfo(
  [property: JsonPropertyName("name")] string Name,
  [property: JsonPropertyName("isVerified")]
  bool IsVerified
);

public sealed record NearestUpdate(
  [property: JsonPropertyName("id")] int Id,
  [property: JsonPropertyName("xmlId")] string XmlId,
  [property: JsonPropertyName("version")]
  string Version,
  [property: JsonPropertyName("products")]
  IReadOnlyList<string> Products,
  [property: JsonPropertyName("updateCompatibility")]
  IReadOnlyDictionary<string, long> UpdateCompatibility,
  [property: JsonPropertyName("isCompatible")]
  bool IsCompatible
);

/// <summary>Item of the compatible-updates endpoints.</summary>
public sealed record IdeCompatibleUpdate(
  [property: JsonPropertyName("id")] int Id,
  [property: JsonPropertyName("pluginId")]
  int PluginId,
  [property: JsonPropertyName("version")]
  string Version,
  [property: JsonPropertyName("pluginXmlId")]
  string PluginXmlId
);

/// <summary>Body of <c>POST /api/search/compatibleUpdates</c> and <c>/api/search/updates/nearest</c>.</summary>
public sealed class CompatibleUpdateRequest {
  [JsonPropertyName("build")] public string? Build { get; set; }

  [JsonPropertyName("pluginXMLIds")] public List<string>? PluginXmlIds { get; set; }

  [JsonPropertyName("module")] public string? Module { get; set; }

  /// <summary>OS enum name (e.g. <c>Linux</c>, <c>macOS</c>), sent by 2025.3+.</summary>
  [JsonPropertyName("os")] public string? Os { get; set; }

  /// <summary>CPU enum name (e.g. <c>X86_64</c>, <c>ARM64</c>), sent by 2025.3+.</summary>
  [JsonPropertyName("arch")] public string? Arch { get; set; }

  public ClientPlatform GetPlatform(HttpRequest request) {
    ClientPlatform platform = ClientPlatform.Parse(Os, Arch);
    return platform.IsKnown ? platform : request.Platform();
  }
}

/// <summary><c>GET /files/{pluginId}/{updateId}/meta.json</c> (IntellijUpdateMetadata).</summary>
public sealed record UpdateMetadata(
  [property: JsonPropertyName("id")] int Id,
  [property: JsonPropertyName("xmlId")] string XmlId,
  [property: JsonPropertyName("name")] string Name,
  [property: JsonPropertyName("description")]
  string Description,
  [property: JsonPropertyName("organization")]
  string Organization,
  [property: JsonPropertyName("vendor")] string Vendor,
  [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
  [property: JsonPropertyName("version")]
  string Version,
  [property: JsonPropertyName("notes")] string Notes,
  [property: JsonPropertyName("dependencies")]
  IReadOnlyList<string> Dependencies,
  [property: JsonPropertyName("optionalDependencies")]
  IReadOnlyList<string> OptionalDependencies,
  [property: JsonPropertyName("depends")]
  IReadOnlyList<string> Depends,
  [property: JsonPropertyName("optionalDepends")]
  IReadOnlyList<string> OptionalDepends,
  [property: JsonPropertyName("pluginAliases")]
  IReadOnlyList<string> PluginAliases,
  [property: JsonPropertyName("since")] string? Since,
  [property: JsonPropertyName("until")] string? Until,
  [property: JsonPropertyName("productCode")]
  string? ProductCode,
  [property: JsonPropertyName("url")] string? Url,
  [property: JsonPropertyName("size")] long Size
);

/// <summary><c>GET /files/{pluginId}/meta.json</c> (IntellijPluginMetadata).</summary>
public sealed record PluginMetadata(
  [property: JsonPropertyName("screenshots")]
  IReadOnlyList<string> Screenshots,
  [property: JsonPropertyName("vendor")] PluginVendorMetadata? Vendor,
  [property: JsonPropertyName("documentationUrl")]
  string? DocumentationUrl,
  [property: JsonPropertyName("sourceCodeUrl")]
  string? SourceCodeUrl,
  [property: JsonPropertyName("forumUrl")]
  string? ForumUrl,
  [property: JsonPropertyName("licenseUrl")]
  string? LicenseUrl,
  [property: JsonPropertyName("bugtrackerUrl")]
  string? BugtrackerUrl
);

public sealed record PluginVendorMetadata(
  [property: JsonPropertyName("name")] string Name,
  [property: JsonPropertyName("url")] string? Url,
  [property: JsonPropertyName("isTrader")]
  bool IsTrader,
  [property: JsonPropertyName("isVerified")]
  bool IsVerified
);

public sealed record AggregationResponse(
  [property: JsonPropertyName("aggregations")]
  IReadOnlyDictionary<string, int> Aggregations,
  [property: JsonPropertyName("total")] int Total
);
