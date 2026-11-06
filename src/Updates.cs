using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MouseBat;

/// <summary>
/// A published release. The download addresses are built from the tag, not taken from the API
/// response, so they can only ever point at this repository's releases.
/// </summary>
public sealed record UpdateRelease(Version Version, string Tag)
{
    public const string LatestApiUrl = "https://api.github.com/repos/OffBy1-tech/MouseBat/releases/latest";
    private const string ReleasesUrl = "https://github.com/OffBy1-tech/MouseBat/releases";

    public string PageUrl => $"{ReleasesUrl}/tag/{Tag}";
    public string ExeUrl => $"{ReleasesUrl}/download/{Tag}/MouseBat.exe";
    public string ChecksumUrl => $"{ReleasesUrl}/download/{Tag}/MouseBat.exe.sha256";

    /// <summary>Reads the GitHub "latest release" response. Returns null if it's unusable.</summary>
    public static UpdateRelease? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("tag_name", out var tagName)) return null;
            if (tagName.ValueKind != JsonValueKind.String) return null;

            string tag = tagName.GetString()!;
            if (!Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z")) return null;
            return Version.TryParse(tag[1..], out var version) ? new UpdateRelease(version, tag) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class UpdateVersion
{
    /// <summary>Reads a build's version, ignoring any suffix: "0.1.1+94111fe" and "0.0.0-dev".</summary>
    public static Version? Parse(string? text)
    {
        string number = (text ?? "").Split('+', '-')[0];
        return Version.TryParse(number, out var version) ? version : null;
    }
}

public static class UpdateChecksum
{
    /// <summary>Checks data against a "hash  filename" checksum file.</summary>
    public static bool Matches(byte[] data, string checksumFile)
    {
        string expected = checksumFile.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return expected.Equals(Convert.ToHexString(SHA256.HashData(data)), StringComparison.OrdinalIgnoreCase);
    }
}
