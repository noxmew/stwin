using System;
using System.Text.Json;

namespace STWin.Windows;

internal readonly record struct DesktopResolution
{
    public const int MinimumDimension = 200;
    public const int MaximumDimension = 8192;
    public static DesktopResolution Default => new(1920, 1080);
    public int Width { get; }
    public int Height { get; }

    public DesktopResolution(int width, int height)
    {
        if (width is < MinimumDimension or > MaximumDimension || height is < MinimumDimension or > MaximumDimension)
            throw new ArgumentException($"分辨率宽高需介于 {MinimumDimension} 至 {MaximumDimension} 像素。");
        Width = width;
        Height = height;
    }

    public static DesktopResolution Parse(JsonElement arguments, bool required = false)
    {
        var hasWidth = arguments.TryGetProperty("width", out var width);
        var hasHeight = arguments.TryGetProperty("height", out var height);
        if (!required && !hasWidth && !hasHeight) return Default;
        if (!hasWidth || !hasHeight || width.ValueKind != JsonValueKind.Number || height.ValueKind != JsonValueKind.Number ||
            !width.TryGetInt32(out var pixelsWide) || !height.TryGetInt32(out var pixelsHigh))
            throw new ArgumentException("分辨率必须同时指定整数 width 和 height。");
        return new(pixelsWide, pixelsHigh);
    }
}
