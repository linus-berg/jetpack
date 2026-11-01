using System.Globalization;

namespace Jetpack.Api.Compatibility;

/// <summary>
///   An IntelliJ Platform build number such as <c>IU-252.25557.131</c> or a
///   plugin compatibility bound such as <c>252.*</c>.
///   Parsing and ordering deliberately mirror
///   <c>com.intellij.openapi.util.BuildNumber</c> so that the server makes the
///   same compatibility decisions as the IDE.
/// </summary>
public sealed class BuildNumber : IComparable<BuildNumber> {
  public const int C_SNAPSHOT_VALUE = int.MaxValue;
  private const string C_SNAPSHOT_ = "SNAPSHOT";
  private const string C_STAR_ = "*";

  private readonly int[] components_;

  private BuildNumber(string product_code, int[] components) {
    ProductCode = product_code;
    components_ = components;
  }

  /// <summary>Product code prefix (e.g. <c>IU</c>), empty when absent.</summary>
  public string ProductCode { get; }

  public IReadOnlyList<int> Components => components_;

  public int CompareTo(BuildNumber? other) {
    if (other is null) {
      return 1;
    }

    int[] c1 = components_;
    int[] c2 = other.components_;
    for (int i = 0; i < Math.Min(c1.Length, c2.Length); i++) {
      if (c1[i] == c2[i] && c1[i] == C_SNAPSHOT_VALUE) {
        return 0;
      }

      if (c1[i] == C_SNAPSHOT_VALUE) {
        return 1;
      }

      if (c2[i] == C_SNAPSHOT_VALUE) {
        return -1;
      }

      int result = c1[i].CompareTo(c2[i]);
      if (result != 0) {
        return result;
      }
    }

    return c1.Length.CompareTo(c2.Length);
  }

  /// <summary>
  ///   Parses a build number; returns <c>null</c> for empty or malformed input
  ///   (the IDE treats an unparseable bound as "no constraint").
  /// </summary>
  public static BuildNumber? TryParse(string? text) {
    if (string.IsNullOrWhiteSpace(text)) {
      return null;
    }

    string code = text.Trim();
    string product_code = "";
    int product_separator = code.IndexOf('-');
    if (product_separator > 0) {
      product_code = code[..product_separator];
      code = code[(product_separator + 1)..];
    }

    if (code == C_SNAPSHOT_ || IsPlaceholder(code)) {
      return new BuildNumber(product_code, [C_SNAPSHOT_VALUE]);
    }

    int baseline_separator = code.IndexOf('.');
    if (baseline_separator > 0) {
      code = code.Replace("-" + C_SNAPSHOT_, "." + C_SNAPSHOT_);
      string[] parts = code.Split('.');
      List<int> components = new(parts.Length);
      foreach (string part in parts) {
        if (!TryParseComponent(part, out int value)) {
          return null;
        }

        components.Add(value);
        if (value == C_SNAPSHOT_VALUE) {
          break;
        }
      }

      return new BuildNumber(product_code, components.ToArray());
    }

    if (!TryParseComponent(code, out int build)) {
      return null;
    }

    // Mirrors the IDE: small numbers are branch baselines, large ones are
    // historic (pre-2013) builds.
    return build <= 2000
             ? new BuildNumber(product_code, [build, 0])
             : new BuildNumber(
               product_code,
               [GetBaselineForHistoricBuilds(build), build]
             );
  }

  /// <summary>
  ///   Returns <c>true</c> when an IDE with the given build can load a plugin
  ///   declaring the given <c>since-build</c> / <c>until-build</c> range.
  /// </summary>
  public static bool IsCompatible(string? since_build, string? until_build,
                                  BuildNumber build) {
    BuildNumber? since = TryParse(since_build);
    if (since != null && since.CompareTo(build) > 0) {
      return false;
    }

    BuildNumber? until = TryParse(until_build);
    return until == null || until.CompareTo(build) >= 0;
  }

  public override string ToString() {
    string components = string.Join(
      '.',
      components_.Select(
        c => c == C_SNAPSHOT_VALUE
               ? C_STAR_
               : c.ToString(CultureInfo.InvariantCulture)
      )
    );
    return ProductCode.Length > 0 ? $"{ProductCode}-{components}" : components;
  }

  private static bool TryParseComponent(string text, out int value) {
    if (text is C_SNAPSHOT_ or C_STAR_ || IsPlaceholder(text)) {
      value = C_SNAPSHOT_VALUE;
      return true;
    }

    return int.TryParse(
      text,
      NumberStyles.None,
      CultureInfo.InvariantCulture,
      out value
    );
  }

  private static bool IsPlaceholder(string text) {
    return text is "__BUILD_NUMBER__" or "__BUILD__";
  }

  private static int GetBaselineForHistoricBuilds(int build) {
    return build switch {
      >= 10000 => 88,
      >= 9500 => 85,
      >= 9100 => 81,
      >= 8000 => 80,
      >= 7500 => 75,
      >= 7200 => 72,
      >= 6900 => 69,
      >= 6500 => 65,
      >= 6000 => 60,
      >= 5000 => 55,
      >= 4000 => 50,
      _ => 40
    };
  }
}
