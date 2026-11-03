using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jetpack.Api.Web;

/// <summary>Display formatting for the web UI.</summary>
public static class Format {
  private static readonly string[] S_SIZE_UNITS_ = ["B", "KB", "MB", "GB", "TB"];

  public static string Number(long value) {
    return value.ToString("N0", CultureInfo.InvariantCulture);
  }

  public static string Bytes(long bytes) {
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < S_SIZE_UNITS_.Length - 1) {
      value /= 1024;
      unit++;
    }

    return unit == 0
             ? $"{bytes} B"
             : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) +
               " " +
               S_SIZE_UNITS_[unit];
  }

  public static string Date(DateTimeOffset value) {
    return value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
  }

  public static string DateTime(DateTimeOffset value) {
    return value.ToUniversalTime()
                .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
  }

  /// <summary>"241 – 252.*", "241+" or "Any build".</summary>
  public static string BuildRange(string? since, string? until) {
    if (since == null && until == null) {
      return "Any build";
    }

    if (until == null) {
      return $"{since}+";
    }

    return $"{since ?? "any"} – {until}";
  }

  /// <summary>Up to two letters for the placeholder icon of plugins without one.</summary>
  public static string Initials(string name) {
    string[] words = name.Split(
      [' ', '-', '_', '.'],
      StringSplitOptions.RemoveEmptyEntries
    );
    string initials = words.Length switch {
      0 => "?",
      1 => words[0][..Math.Min(2, words[0].Length)],
      _ => $"{words[0][0]}{words[1][0]}"
    };
    return initials.ToUpperInvariant();
  }

  /// <summary>Stable colour bucket (0-7) for a plugin's placeholder icon.</summary>
  public static int Hue(string plugin_id) {
    return SHA256.HashData(Encoding.UTF8.GetBytes(plugin_id))[0] % 8;
  }

  public static string Truncate(string text, int length) {
    return text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";
  }
}
