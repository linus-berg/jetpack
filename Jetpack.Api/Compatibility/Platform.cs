using System.Text.RegularExpressions;

namespace Jetpack.Api.Compatibility;

/// <summary>Operating system of an IDE (mirrors <c>com.intellij.util.system.OS</c>).</summary>
public enum HostOs {
  WINDOWS,
  MAC,
  LINUX,
  FREE_BSD,
  OHOS,
  OTHER
}

/// <summary>CPU architecture of an IDE (mirrors <c>com.intellij.util.system.CpuArch</c>).</summary>
public enum HostArch {
  X86,
  X86_64,
  ARM32,
  ARM64,
  OTHER
}

/// <summary>
///   The platform an IDE reports. IDEs 2025.3+ send it as <c>os</c> (e.g.
///   <c>Linux 6.19.10</c>, <c>Windows 11</c>, <c>macOS 15.1</c>) and
///   <c>arch</c> (e.g. <c>X86_64</c>, <c>ARM64</c>); older IDEs send nothing,
///   which leaves both values unknown.
/// </summary>
public sealed record ClientPlatform(HostOs? Os, HostArch? Arch) {
  public static readonly ClientPlatform S_UNKNOWN = new(null, null);

  public bool IsKnown => Os != null || Arch != null;

  public static ClientPlatform Parse(string? os, string? arch) {
    return new ClientPlatform(ParseOs(os), ParseArch(arch));
  }

  /// <summary>Like <c>OS.fromString</c>, plus the IDE's own enum names.</summary>
  public static HostOs? ParseOs(string? text) {
    if (string.IsNullOrWhiteSpace(text)) {
      return null;
    }

    string os = text.Trim().ToLowerInvariant();
    if (os.StartsWith("windows", StringComparison.Ordinal)) {
      return HostOs.WINDOWS;
    }

    if (os.StartsWith("mac", StringComparison.Ordinal)) {
      return HostOs.MAC;
    }

    if (os.StartsWith("linux", StringComparison.Ordinal)) {
      return HostOs.LINUX;
    }

    if (os.StartsWith("freebsd", StringComparison.Ordinal)) {
      return HostOs.FREE_BSD;
    }

    if (os.StartsWith("ohos", StringComparison.Ordinal) ||
        os.Contains("harmony", StringComparison.Ordinal)) {
      return HostOs.OHOS;
    }

    return HostOs.OTHER;
  }

  /// <summary>Accepts <c>CpuArch</c> names as well as <c>os.arch</c> style values.</summary>
  public static HostArch? ParseArch(string? text) {
    if (string.IsNullOrWhiteSpace(text)) {
      return null;
    }

    return text.Trim().ToLowerInvariant() switch {
      "x86_64" or "amd64" or "x64" => HostArch.X86_64,
      "x86" or "i386" => HostArch.X86,
      "arm64" or "aarch64" => HostArch.ARM64,
      "arm32" or "arm" => HostArch.ARM32,
      "unknown" => null,
      _ => HostArch.OTHER
    };
  }
}

/// <summary>
///   OS / CPU requirements of a plugin version, derived exactly like the IDE
///   does (<c>PluginCompatibilityUtils.getUnfulfilledOsRequirement</c> and
///   <c>getUnfulfilledCpuArchRequirement</c>): required dependencies on
///   <c>com.intellij.modules.os.*</c> and <c>com.intellij.modules.arch.*</c>, or,
///   for plugins without any dependencies, a <c>version-os-arch</c> suffix.
///   Platform-specific builds (e.g. GitHub Copilot's
///   <c>1.20.0-261-linux-x64</c>) declare these.
/// </summary>
public sealed partial class PlatformRequirement {
  private const string C_OS_PREFIX_ = "com.intellij.modules.os.";
  private const string C_ARCH_PREFIX_ = "com.intellij.modules.arch.";

  public static readonly PlatformRequirement S_NONE = new([], [], null, null);

  private readonly HostArch? inferred_arch_;
  private readonly HostOs? inferred_os_;

  private PlatformRequirement(IReadOnlyList<string> os_modules,
                              IReadOnlyList<string> arch_modules,
                              HostOs? inferred_os, HostArch? inferred_arch) {
    OsModules = os_modules;
    ArchModules = arch_modules;
    inferred_os_ = inferred_os;
    inferred_arch_ = inferred_arch;
  }

  /// <summary>Required OS module suffixes, e.g. <c>linux</c>, <c>unix</c>.</summary>
  public IReadOnlyList<string> OsModules { get; }

  /// <summary>Required CPU module suffixes, e.g. <c>x86_64</c>, <c>arm64</c>.</summary>
  public IReadOnlyList<string> ArchModules { get; }

  public bool IsPlatformSpecific =>
    OsModules.Count > 0 ||
    ArchModules.Count > 0 ||
    inferred_os_ != null ||
    inferred_arch_ != null;

  /// <summary>Short label such as <c>linux/x86_64</c>, or null when platform independent.</summary>
  public string? Label {
    get {
      if (!IsPlatformSpecific) {
        return null;
      }

      IEnumerable<string> os = OsModules.Count > 0
                                 ? OsModules
                                 : inferred_os_ is { } o
                                   ? [o.ToString().ToLowerInvariant()]
                                   : ["any"];
      IEnumerable<string> arch = ArchModules.Count > 0
                                   ? ArchModules
                                   : inferred_arch_ is { } a
                                     ? [a.ToString().ToLowerInvariant()]
                                     : ["any"];
      return $"{string.Join('+', os)}/{string.Join('+', arch)}";
    }
  }

  /// <param name="dependencies">Required (non-optional) dependencies.</param>
  public static PlatformRequirement FromRelease(
    IReadOnlyCollection<string> dependencies, string version) {
    if (dependencies.Count == 0) {
      // The IDE infers the platform from versions like "1.0-linux-arm64" only
      // when a plugin declares no dependencies at all.
      Match match = OsArchVersion().Match(version);
      if (!match.Success) {
        return S_NONE;
      }

      HostOs? os = ClientPlatform.ParseOs(match.Groups[2].Value);
      HostArch? arch = InferArch(match.Groups[3].Value);
      return new PlatformRequirement(
        [],
        [],
        os == HostOs.OTHER ? null : os,
        arch
      );
    }

    List<string> os_modules = dependencies
                              .Where(d => d.StartsWith(C_OS_PREFIX_, StringComparison.Ordinal))
                              .Select(d => d[C_OS_PREFIX_.Length..])
                              .Distinct(StringComparer.Ordinal)
                              .ToList();
    List<string> arch_modules = dependencies
                                .Where(d => d.StartsWith(C_ARCH_PREFIX_, StringComparison.Ordinal))
                                .Select(d => d[C_ARCH_PREFIX_.Length..])
                                .Distinct(StringComparer.Ordinal)
                                .ToList();
    return os_modules.Count == 0 && arch_modules.Count == 0
             ? S_NONE
             : new PlatformRequirement(os_modules, arch_modules, null, null);
  }

  /// <returns>
  ///   <c>true</c> if the client satisfies every requirement, <c>false</c> if it
  ///   violates one, <c>null</c> if that can't be decided because the client
  ///   did not report its OS or architecture.
  /// </returns>
  public bool? Evaluate(ClientPlatform client) {
    bool undecided = false;
    foreach (string module in OsModules) {
      if (client.Os is not { } os) {
        undecided = true;
      } else if (!OsModuleMatches(module, os)) {
        return false;
      }
    }

    if (inferred_os_ is { } required_os) {
      if (client.Os is not { } os) {
        undecided = true;
      } else if (os != required_os) {
        return false;
      }
    }

    foreach (string module in ArchModules) {
      if (client.Arch is not { } arch) {
        undecided = true;
      } else if (!ArchModuleMatches(module, arch)) {
        return false;
      }
    }

    if (inferred_arch_ is { } required_arch) {
      if (client.Arch is not { } arch) {
        undecided = true;
      } else if (arch != required_arch) {
        return false;
      }
    }

    return undecided ? null : true;
  }

  // IdeaPluginOsRequirement: unknown "com.intellij.modules.os.*" modules never
  // match.
  private static bool OsModuleMatches(string module, HostOs os) {
    return module switch {
      "windows" => os == HostOs.WINDOWS,
      "mac" => os == HostOs.MAC,
      "linux" => os == HostOs.LINUX,
      "freebsd" => os == HostOs.FREE_BSD,
      "ohos" => os == HostOs.OHOS,
      "unix" => os != HostOs.WINDOWS,
      "xwindow" => os != HostOs.WINDOWS && os != HostOs.MAC,
      _ => false
    };
  }

  // PluginCpuArchRequirement: unknown "com.intellij.modules.arch.*" modules
  // never match.
  private static bool ArchModuleMatches(string module, HostArch arch) {
    return module switch {
      "x86" => arch == HostArch.X86,
      "x86_64" => arch == HostArch.X86_64,
      "arm32" => arch == HostArch.ARM32,
      "arm64" => arch == HostArch.ARM64,
      _ => false
    };
  }

  // CpuArch.fromString as used for version suffixes (only these spellings).
  private static HostArch? InferArch(string tag) {
    return tag switch {
      "x86_64" or "amd64" => HostArch.X86_64,
      "i386" or "x86" => HostArch.X86,
      "aarch64" or "arm64" => HostArch.ARM64,
      _ => null
    };
  }

  [GeneratedRegex("^([A-Za-z0-9_.]+)-([A-Za-z0-9_]+)-([A-Za-z0-9_]+)$")]
  private static partial Regex OsArchVersion();
}
