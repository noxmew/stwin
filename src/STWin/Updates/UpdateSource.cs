using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace STWin.Updates;

internal static class UpdateSource
{
    public static async Task<UpdateRelease> ReadAsync(string? version, CancellationToken token)
    {
        if (version is not null && (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") || !Version.TryParse(version, out _)))
            throw new ArgumentException("更新版本号无效。");
        using var client = Client();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var endpoint = version is null ? "latest" : $"tags/v{version}";
        using var response = await client.GetAsync($"https://api.github.com/repos/noxmew/stwin/releases/{endpoint}", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("更新必须来自正式 Release。");
        var tag = root.GetProperty("tag_name").GetString()!;
        if (!tag.StartsWith('v') || version is not null && tag != $"v{version}")
            throw new InvalidDataException("Release 版本标签不匹配。");
        var asset = root.GetProperty("assets").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "STWin.exe");
        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("Release 缺少 SHA256 校验值。");
        var release = new UpdateRelease(tag[1..], asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), digest[7..]);
        release.Validate();
        return release;
    }

    public static async Task DownloadAsync(UpdateRelease release, string path, CancellationToken token)
    {
        release.Validate();
        using var client = Client();
        using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("更新文件大小与 Release 不一致。");
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                total += count;
                if (total > release.Size) throw new InvalidDataException("更新文件超出声明的大小。");
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        await VerifyAsync(path, release, token);
    }

    public static async Task VerifyAsync(string path, UpdateRelease release, CancellationToken token)
    {
        var version = release.Validate();
        if (new FileInfo(path).Length != release.Size) throw new InvalidDataException("更新文件大小不一致。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("更新文件 SHA256 校验失败。");
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var actual) || UpdateRelease.Normalize(actual) != version)
            throw new InvalidDataException("更新程序版本与 Release 不一致。");
    }

    private static HttpClient Client()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"STWin/{UpdateRelease.CurrentVersionText}");
        return client;
    }
}
