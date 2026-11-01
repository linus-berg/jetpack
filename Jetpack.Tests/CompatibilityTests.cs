using Jetpack.Api.Compatibility;

namespace Jetpack.Tests;

public class BuildNumberTests {
  [Theory]
  [InlineData("IU-252.25557.131", "IU", new[] { 252, 25557, 131 })]
  [InlineData("252.25557", "", new[] { 252, 25557 })]
  [InlineData("241", "", new[] { 241, 0 })]
  [InlineData("WS-262.11879.105", "WS", new[] { 262, 11879, 105 })]
  public void ParsesLikeTheIde(string text, string product, int[] components) {
    BuildNumber build = BuildNumber.TryParse(text)!;
    Assert.Equal(product, build.ProductCode);
    Assert.Equal(components, build.Components);
  }

  [Fact]
  public void WildcardIsMaximal() {
    BuildNumber star = BuildNumber.TryParse("252.*")!;
    Assert.Equal([252, BuildNumber.C_SNAPSHOT_VALUE], star.Components);
    Assert.True(star.CompareTo(BuildNumber.TryParse("252.99999.999")) > 0);
    Assert.True(star.CompareTo(BuildNumber.TryParse("253.1")) < 0);
  }

  [Theory]
  [InlineData(null, null, "IU-252.1.1", true)]
  [InlineData("241", null, "IU-252.1.1", true)]
  [InlineData("241", "252.*", "IU-252.28238.7", true)]
  [InlineData("241", "251.*", "IU-252.1.1", false)]
  [InlineData("253", null, "IU-252.1.1", false)]
  [InlineData("252.25557", null, "IU-252.25557.131", true)]
  [InlineData("252.25558", null, "IU-252.25557.131", false)]
  // until-build "252" means 252.0, so 252.x builds are NOT compatible.
  [InlineData(null, "252", "IU-252.1.1", false)]
  [InlineData("not-a-build", "also bad", "IU-252.1.1", true)]
  public void CompatibilityMatchesIde(string? since, string? until,
                                      string build, bool expected) {
    Assert.Equal(
      expected,
      BuildNumber.IsCompatible(since, until, BuildNumber.TryParse(build)!)
    );
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("abc.def")]
  [InlineData(".5")]
  public void RejectsGarbage(string text) {
    Assert.Null(BuildNumber.TryParse(text));
  }
}

public class VersionComparatorTests {
  [Theory]
  [InlineData("1.0", "1.0.1")]
  [InlineData("1.9", "1.10")]
  [InlineData("1.0-SNAPSHOT", "1.0")]
  [InlineData("1.0rc1", "1.0")]
  [InlineData("1.0-beta", "1.0-rc")]
  [InlineData("1.0-alpha", "1.0-beta")]
  [InlineData("2025.3.9", "2025.3.12")]
  [InlineData("0.4.200.5420-241", "0.4.201.5424-241")]
  [InlineData("241.14494.240", "242.20224.300")]
  public void OrdersLikeTheIde(string lower, string higher) {
    Assert.True(VersionComparator.CompareVersions(lower, higher) < 0);
    Assert.True(VersionComparator.CompareVersions(higher, lower) > 0);
  }

  [Theory]
  [InlineData("1.0", "1")]
  [InlineData("1.0.0", "1")]
  [InlineData("V1.0", "v1.0")]
  public void TreatsEquivalentVersionsAsEqual(string a, string b) {
    Assert.Equal(0, VersionComparator.CompareVersions(a, b));
  }
}
