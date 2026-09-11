using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuckPane.Updates;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Digest);
public sealed record UpdateRelease(string Version, string Notes, UpdateAsset Package, UpdateAsset Checksums);

public static class ReleaseRules
{
    public const string Repository = "ch998244353/TuckPane";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public static Version StableVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException("Invalid stable version.");
        return Version.Parse(value.TrimStart('v'));
    }

    public static bool AutoCheckDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is null || now < lastAttempt || now - lastAttempt >= TimeSpan.FromHours(24);

    public static UpdateRelease? Parse(string json, string currentVersion, bool installed)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        Version latest = StableVersion(tag);
        if (latest <= StableVersion(currentVersion)) return null;
        string name = $"TuckPane-{latest}-win-x64-{(installed ? "setup.exe" : "portable.zip")}";
        UpdateAsset ReadAsset(string expected)
        {
            JsonElement[] matches = root.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == expected).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Required release asset is missing or duplicated.");
            JsonElement item = matches[0];
            string url = item.GetProperty("browser_download_url").GetString() ?? "";
            string expectedUrl = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{expected}";
            if (!string.Equals(url, expectedUrl, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release source.");
            long size = item.GetProperty("size").GetInt64();
            if (size <= 0 || size > 1_500_000_000) throw new InvalidDataException("Invalid asset size.");
            return new(expected, url, size, item.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
        }
        return new(latest.ToString(), root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            ReadAsset(name), ReadAsset("SHA256SUMS.txt"));
    }

    public static string ExpectedHash(string checksums, string fileName)
    {
        MatchCollection matches = Regex.Matches(checksums, @"(?m)^([a-fA-F0-9]{64}) [ *]" + Regex.Escape(fileName) + @"\r?$");
        if (matches.Count != 1) throw new InvalidDataException("Missing or duplicate package checksum.");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
}

public sealed class ReleaseClient(HttpClient http)
{
    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TuckPane-Updater/4.0.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    public async Task<string> GetLatestJsonAsync(CancellationToken token)
    {
        using var request = Request(ReleaseRules.LatestUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1_000_000, token);
        return await response.Content.ReadAsStringAsync(token);
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var checksumRequest = Request(release.Checksums.Url);
        using var checksumResponse = await http.SendAsync(checksumRequest, HttpCompletionOption.ResponseHeadersRead, token);
        checksumResponse.EnsureSuccessStatusCode();
        await checksumResponse.Content.LoadIntoBufferAsync(65_536, token);
        string hash = ReleaseRules.ExpectedHash(await checksumResponse.Content.ReadAsStringAsync(token), release.Package.Name);
        if (release.Package.Digest is { Length: > 0 } digest && !string.Equals(digest, "sha256:" + hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release digest and checksums disagree.");
        string destination = Path.Combine(directory, release.Package.Name);
        string partial = destination + ".download";
        try
        {
            using var request = Request(release.Package.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    length += count;
                    if (length > release.Package.Size) throw new InvalidDataException("Download exceeds asset size.");
                    checksum.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report(length * 100d / release.Package.Size);
                }
                if (length != release.Package.Size || !Convert.ToHexString(checksum.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package checksum or length mismatch.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
