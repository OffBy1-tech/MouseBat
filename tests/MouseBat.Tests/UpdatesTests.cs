using System.Security.Cryptography;
using System.Text;
using MouseBat;

namespace MouseBat.Tests;

public class UpdateReleaseTests
{
    [Fact]
    public void ReadsTagFromGitHubResponse()
    {
        var release = UpdateRelease.Parse("""{"tag_name":"v0.2.10","draft":false,"name":"v0.2.10"}""")!;

        Assert.Equal(new Version(0, 2, 10), release.Version);
        Assert.Equal("v0.2.10", release.Tag);
    }

    [Fact]
    public void BuildsAddressesInThisRepository()
    {
        var release = UpdateRelease.Parse("""{"tag_name":"v0.2.0"}""")!;

        Assert.Equal("https://github.com/OffBy1-tech/MouseBat/releases/tag/v0.2.0", release.PageUrl);
        Assert.Equal("https://github.com/OffBy1-tech/MouseBat/releases/download/v0.2.0/MouseBat.exe", release.ExeUrl);
        Assert.Equal("https://github.com/OffBy1-tech/MouseBat/releases/download/v0.2.0/MouseBat.exe.sha256", release.ChecksumUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.2.0/../../evil"}""")]
    [InlineData("""{"tag_name":"v0.2.0\n"}""")]
    [InlineData("""{"tag_name":"0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2"}""")]
    [InlineData("""{"tag_name":"v0.2.0-beta"}""")]
    [InlineData("""{"tag_name":"v99999999999.0.0"}""")]
    [InlineData("""{"tag_name":7}""")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void RejectsUnusableResponses(string json) => Assert.Null(UpdateRelease.Parse(json));
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.1.1+94111fe54cb9", "0.1.1")]
    [InlineData("0.0.0-dev", "0.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    public void IgnoresBuildSuffixes(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    public void UnreadableVersionsAreNull(string? text) => Assert.Null(UpdateVersion.Parse(text));

    [Fact]
    public void ComparesNumbersNotText() =>
        Assert.True(UpdateVersion.Parse("0.10.0") > UpdateVersion.Parse("0.9.0"));
}

public class UpdateChecksumTests
{
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("MouseBat");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Data)).ToLowerInvariant();

    [Fact]
    public void AcceptsMatchingFileInReleaseFormat() =>
        Assert.True(UpdateChecksum.Matches(Data, $"{Hash}  MouseBat.exe\n"));

    [Fact]
    public void IgnoresCase() =>
        Assert.True(UpdateChecksum.Matches(Data, Hash.ToUpperInvariant()));

    [Fact]
    public void RejectsChangedData() =>
        Assert.False(UpdateChecksum.Matches(Encoding.ASCII.GetBytes("MouseBaT"), $"{Hash}  MouseBat.exe\n"));

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("<html>Not Found</html>")]
    public void RejectsUnusableChecksumFiles(string file) =>
        Assert.False(UpdateChecksum.Matches(Data, file));
}
