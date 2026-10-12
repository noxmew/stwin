using System;
using System.IO;
using System.Text.RegularExpressions;
using STWin.Infrastructure;

namespace STWin.Updates;

internal sealed record UpdateRelease(string Version, string DownloadUrl, long Size, string Sha256)
{
    public static System.Version CurrentVersion { get; } = Normalize(typeof(App).Assembly.GetName().Version!);
    public static string CurrentVersionText => CurrentVersion.ToString(3);
    public const long MaximumSize = 512L * 1024 * 1024;

    public System.Version Validate()
    {
        if (!Regex.IsMatch(Version, @"^\d+\.\d+\.\d+$") || !System.Version.TryParse(Version, out var version))
            throw new InvalidDataException("更新版本号无效。");
        if (DownloadUrl != $"https://github.com/noxmew/stwin/releases/download/v{Version}/STWin.exe")
            throw new InvalidDataException("更新下载地址无效。");
        if (Size <= 0 || Size > MaximumSize || !Regex.IsMatch(Sha256, @"^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("更新文件缺少有效的大小或 SHA256 校验值。");
        return version;
    }

    public static System.Version Normalize(System.Version version) => new(version.Major, version.Minor, version.Build);
}

internal sealed record DownloadedUpdate(UpdateRelease Release, string StageId)
{
    public int ClientProcessId { get; init; }
    public bool HiddenHost { get; init; }
    public static string StagingRoot => Path.Combine(Settings.Folder, "updates");
    public string DirectoryPath => StageDirectory(StageId);
    public string ExecutablePath => Path.Combine(DirectoryPath, "STWin.exe");

    public static string StageDirectory(string stageId)
    {
        if (!Guid.TryParseExact(stageId, "N", out _)) throw new InvalidDataException("更新暂存目录无效。");
        return Path.Combine(StagingRoot, stageId);
    }
}
