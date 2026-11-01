using System.Text.RegularExpressions;

namespace Jetpack.Api.Compatibility;

/// <summary>
///   Port of <c>com.intellij.util.text.VersionComparatorUtil</c>.
///   The IDE uses this to decide whether a version offered by the server is an
///   update, so "latest version" here must use exactly the same ordering.
/// </summary>
public sealed partial class VersionComparator : IComparer<string?> {
  private static readonly char[] S_DELIMITERS_ =
    ['(', ')', '.', '_', '-', ';', ':', '/', ',', ' ', '+', '~'];

  public static readonly VersionComparator S_INSTANCE = new();

  public int Compare(string? x, string? y) {
    return CompareVersions(x, y);
  }

  public static int CompareVersions(string? ver1, string? ver2) {
    if (ver1 == null) {
      return ver2 == null ? 0 : -1;
    }

    if (ver2 == null) {
      return 1;
    }

    List<string?> s1 = Split(ver1.ToLowerInvariant());
    List<string?> s2 = Split(ver2.ToLowerInvariant());
    while (s1.Count < s2.Count) {
      s1.Add(null);
    }

    while (s2.Count < s1.Count) {
      s2.Add(null);
    }

    for (int i = 0; i < s1.Count; i++) {
      string? e1 = s1[i];
      string? e2 = s2[i];
      TokenType t1 = Lookup(e1);
      int result = Priority(t1).CompareTo(Priority(Lookup(e2)));
      if (result != 0) {
        return result;
      }

      if (t1 == TokenType.WORD) {
        result = string.CompareOrdinal(e1, e2);
      } else if (t1 == TokenType.DIGITS) {
        result = CompareNumbers(e1!, e2!);
      }

      if (result != 0) {
        return Math.Sign(result);
      }
    }

    return 0;
  }

  private static List<string?> Split(string version) {
    List<string?> result = new();
    foreach (string token in version.Trim()
                                    .Split(
                                      S_DELIMITERS_,
                                      StringSplitOptions.RemoveEmptyEntries
                                    )) {
      foreach (Match match in WordsSplitter().Matches(token)) {
        result.Add(match.Value);
      }
    }

    return result;
  }

  private static int CompareNumbers(string n1, string n2) {
    while (n1.Length > 0 && n2.Length > 0 && n1[0] == '0' && n2[0] == '0') {
      n1 = n1[1..];
      n2 = n2[1..];
    }

    if (n1.Length > 0 && n1[0] == '0') {
      return -1;
    }

    if (n2.Length > 0 && n2[0] == '0') {
      return 1;
    }

    if (n1.Length != n2.Length) {
      return n1.Length.CompareTo(n2.Length);
    }

    return string.CompareOrdinal(n1, n2);
  }

  private static TokenType Lookup(string? token) {
    if (token == null) {
      return TokenType.WS;
    }

    string trimmed = token.Trim();
    if (trimmed.Length == 0) {
      return TokenType.WS;
    }

    switch (trimmed) {
      case "snap":
      case "snapshot":
        return TokenType.SNAPSHOT;
      case "m":
        return TokenType.MILESTONE;
      case "eap":
      case "pre":
      case "preview":
        return TokenType.EAP;
      case "alpha":
      case "a":
        return TokenType.ALPHA;
      case "beta":
      case "betta":
      case "b":
        return TokenType.BETA;
      case "rc":
        return TokenType.RC;
      case "sp":
        return TokenType.SP;
      case "rel":
      case "release":
      case "r":
      case "final":
        return TokenType.RELEASE;
      case "bundled":
        return TokenType.BUNDLED;
    }

    if (trimmed.All(c => c == '0')) {
      return TokenType.WS;
    }

    return trimmed.All(char.IsAsciiDigit) ? TokenType.DIGITS : TokenType.WORD;
  }

  private static int Priority(TokenType type) {
    return type switch {
      TokenType.SNAPSHOT => 10,
      TokenType.MILESTONE => 20,
      TokenType.EAP => 25,
      TokenType.ALPHA => 30,
      TokenType.BETA => 40,
      TokenType.RC => 50,
      TokenType.WS => 60,
      TokenType.SP => 70,
      TokenType.RELEASE => 80,
      TokenType.WORD => 90,
      TokenType.DIGITS => 100,
      TokenType.BUNDLED => 666,
      _ => 0
    };
  }

  [GeneratedRegex("[0-9]+|[^0-9]+", RegexOptions.CultureInvariant)]
  private static partial Regex WordsSplitter();

  private enum TokenType {
    SNAPSHOT,
    MILESTONE,
    EAP,
    ALPHA,
    BETA,
    RC,
    WS,
    SP,
    RELEASE,
    WORD,
    DIGITS,
    BUNDLED
  }
}
