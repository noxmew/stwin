using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using STWin.Infrastructure;

namespace STWin.Updates;

// This code runs only in a dedicated process, before the desktop and MCP are initialized.
internal static class UpdateWorker
{
    public const string Argument = "--update-worker";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            switch (args)
            {
                case [Argument, "check"]:
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(await CheckAsync()));
                    break;
                case [Argument, "download", var releaseJson, var stageId]:
                    var release = JsonSerializer.Deserialize<UpdateRelease>(releaseJson)
                        ?? throw new InvalidDataException("更新信息为空。");
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(await DownloadAsync(release, stageId)));
                    break;
                case [Argument, "apply", var downloadJson, var pidText, var target]:
                    var download = JsonSerializer.Deserialize<DownloadedUpdate>(downloadJson)
                        ?? throw new InvalidDataException("下载信息为空。");
                    await ApplyAsync(download, int.Parse(pidText), target);
                    break;
                default:
                    throw new ArgumentException("更新进程参数无效。");
            }
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex is OperationCanceledException ? "更新请求超时，请稍后再试。" : ex.Message);
            Settings.Log($"更新进程失败：{ex}");
            return 1;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"STWin/{UpdateRelease.CurrentVersionText}");
        return client;
    }

    private static async Task<UpdateRelease?> CheckAsync()
    {
        CleanupOldDownloads();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync("https://api.github.com/repos/noxmew/stwin/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString()!;
        if (!tag.StartsWith('v')) throw new InvalidDataException("Release 版本标签无效。");
        var asset = root.GetProperty("assets").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "STWin.exe");
        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("Release 缺少 SHA256 校验值。");
        var release = new UpdateRelease(tag[1..], asset.GetProperty("browser_download_url").GetString()!,
            asset.GetProperty("size").GetInt64(), digest[7..]);
        return release.Validate() > UpdateRelease.CurrentVersion ? release : null;
    }

    private static async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, string stageId)
    {
        if (release.Validate() <= UpdateRelease.CurrentVersion) throw new InvalidDataException("更新版本必须高于当前版本。");
        var download = new DownloadedUpdate(release, stageId);
        Directory.CreateDirectory(download.DirectoryPath);
        var partial = Path.Combine(download.DirectoryPath, "download.tmp");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            using var client = CreateClient();
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("更新文件大小与 Release 不一致。");
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("更新文件超出声明的大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
            }
            await VerifyAsync(partial, release, timeout.Token);
            File.Move(partial, download.ExecutablePath);
            return download;
        }
        catch
        {
            DeleteStage(stageId);
            throw;
        }
    }

    private static async Task VerifyAsync(string path, UpdateRelease release, CancellationToken token)
    {
        var version = release.Validate();
        if (new FileInfo(path).Length != release.Size) throw new InvalidDataException("更新文件大小不一致。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("更新文件 SHA256 校验失败。");
        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!Version.TryParse(fileVersion, out var actualVersion) || UpdateRelease.Normalize(actualVersion) != version)
            throw new InvalidDataException("更新程序版本与 Release 不一致。");
    }

    private static async Task ApplyAsync(DownloadedUpdate download, int parentId, string targetPath)
    {
        download.Release.Validate();
        var target = Path.GetFullPath(targetPath);
        if (!string.Equals(Environment.ProcessPath, download.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, download.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新安装程序路径无效。");
        using var parent = Process.GetProcessById(parentId);
        if (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("正在运行的程序与更新目标不一致。");
        if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("程序文件为只读，无法更新。");
        var temporary = UpdateInstaller.TemporaryPath(download, target);
        try
        {
            // Prepare and verify a sibling file before asking the running app to exit.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                await using (var source = new FileStream(download.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(destination, timeout.Token);
                await VerifyAsync(temporary, download.Release, timeout.Token);
            }
            await Console.Out.WriteLineAsync("READY");
            await Console.Out.FlushAsync();
            using (var approvalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                // Console.In may perform synchronous reads even through ReadLineAsync.
                var approval = Task.Run(Console.In.ReadLine);
                var parentExited = parent.WaitForExitAsync(approvalTimeout.Token);
                try
                {
                    await Task.WhenAny(approval, parentExited);
                    if (!approval.IsCompleted || await approval != "INSTALL")
                        throw new InvalidOperationException("更新安装已取消。");
                }
                finally { approvalTimeout.Cancel(); }
            }
            await Console.Out.WriteLineAsync("ACCEPTED");
            await Console.Out.FlushAsync();
            try
            {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(exitTimeout.Token);
                File.Replace(temporary, target, null);
                var start = new ProcessStartInfo(target) { UseShellExecute = false };
                start.ArgumentList.Add("--update-cleanup");
                start.ArgumentList.Add(download.StageId);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                using var next = Process.Start(start) ?? throw new InvalidOperationException("更新后无法启动程序。");
            }
            catch (Exception ex)
            {
                Settings.Log($"安装更新失败：{ex}");
                MessageBox.Show($"更新未完成：{ex.Message}\n程序位置：{target}", "STWin 更新", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task CleanupAsync(string stageId, int installerId)
    {
        try
        {
            _ = DownloadedUpdate.StageDirectory(stageId);
            try
            {
                using var installer = Process.GetProcessById(installerId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await installer.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            DeleteStage(stageId);
        }
        catch (Exception ex) { Settings.Log($"清理更新文件失败：{ex.Message}"); }
    }

    private static void CleanupOldDownloads()
    {
        if (!Directory.Exists(DownloadedUpdate.StagingRoot)) return;
        foreach (var path in Directory.EnumerateDirectories(DownloadedUpdate.StagingRoot))
        {
            var name = Path.GetFileName(path);
            if (Guid.TryParseExact(name, "N", out _) && Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                DeleteStage(name);
        }
    }

    private static void DeleteStage(string stageId)
    {
        try
        {
            var path = DownloadedUpdate.StageDirectory(stageId);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) { Settings.Log($"清理更新暂存目录失败：{ex.Message}"); }
    }
}
