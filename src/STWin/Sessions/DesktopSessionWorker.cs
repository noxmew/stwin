using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using STWin.Commands;
using STWin.Infrastructure;
using STWin.Windows;

namespace STWin.Sessions;

internal sealed record DesktopSessionRequest(DesktopCommand Command, int Width, int Height);

internal static class DesktopSessionWorker
{
    public const string Argument = "--desktop-worker";
    private const string PipePrefix = "STWin.desktop.";

    public static async Task<CommandReply> ExecuteAsync(uint sessionId, DesktopCommand command, Size size, CancellationToken cancellationToken)
    {
        var elevated = command.Name switch
        {
            "take_screenshot" => false,
            "mouse_move" or "mouse_click" or "mouse_scroll" => true,
            _ => throw new ArgumentException("不支持的分身桌面操作。")
        };
        if (command.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("桌面操作参数必须是 JSON 对象。");
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("尚未获取分身桌面的实际分辨率。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        try
        {
            if (SessionApi.ChildId() != sessionId) throw new InvalidOperationException("分身会话已变化，请重新连接。");
            var name = $"{PipePrefix}{Guid.NewGuid():N}";
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            var security = new PipeSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            // Only mouse input needs elevation to reach elevated apps in the clone.
            var launch = new ProgramLaunchRequest(sessionId, Environment.ProcessPath!,
                [Argument, sessionId.ToString(), name, Environment.ProcessId.ToString()], null, elevated);
            var launched = await ProgramLauncher.LaunchAsync(launch, token).ConfigureAwait(false);
            using var worker = SessionProcess.OpenForInspection(checked((uint)launched.ProcessId));
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            var workerId = SessionProcess.PipeClientProcessId(pipe);
            if (workerId != launched.ProcessId || SessionProcess.SessionId(workerId) != sessionId ||
                SessionProcess.UserSid(workerId) != sid.Value ||
                !string.Equals(SessionProcess.ImagePath(workerId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                elevated && !SessionProcess.IsElevated(workerId))
                throw new UnauthorizedAccessException("分身桌面助手身份不匹配。");
            if (SessionApi.ChildId() != sessionId) throw new InvalidOperationException("分身会话已变化，已取消操作。");
            await CommandPipe.WriteAsync(pipe, new DesktopSessionRequest(command, size.Width, size.Height), CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            var reply = await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumReply, token).ConfigureAwait(false);
            var exitCode = await SessionProcess.WaitForExitAsync(worker, token).ConfigureAwait(false);
            if (!reply.Ok) throw new CommandFailedException(reply.Error ?? "分身桌面操作失败。", reply.ExitCode);
            if (exitCode != 0) throw new InvalidOperationException("分身桌面助手没有正常退出，请检查分身状态。");
            if (SessionApi.ChildId() != sessionId) throw new InvalidOperationException("操作期间分身会话已变化。");
            return reply;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("分身桌面操作超时，请查询状态后再操作。"); }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [Argument, var sessionText, var name, var parentText] ||
            !uint.TryParse(sessionText, out var sessionId) || sessionId is 0 or uint.MaxValue ||
            !uint.TryParse(parentText, out var parentId) ||
            !name.StartsWith(PipePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[PipePrefix.Length..], "N", out _)) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            if (SessionProcess.SessionId((uint)Environment.ProcessId) != sessionId)
                throw new UnauthorizedAccessException("桌面助手不在指定分身会话中。");
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            using var identity = WindowsIdentity.GetCurrent();
            var parentSession = SessionProcess.SessionId(parentId);
            if (SessionProcess.PipeServerProcessId(pipe) != parentId || parentSession == 0 || parentSession == sessionId ||
                SessionProcess.UserSid(parentId) != identity.User?.Value ||
                !string.Equals(SessionProcess.ImagePath(parentId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("桌面助手没有连接到原登录会话中的 STWin。");
            var request = await CommandPipe.ReadAsync<DesktopSessionRequest>(pipe, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            var display = Screen.PrimaryScreen ?? throw new InvalidOperationException("无法读取分身桌面的显示器。");
            var bounds = display.Bounds;
            if (bounds.Width != request.Width || bounds.Height != request.Height)
                throw new InvalidOperationException($"分身桌面当前为 {bounds.Width} × {bounds.Height}，与请求的 {request.Width} × {request.Height} 不一致；请重新获取桌面状态和截图。");
            token.ThrowIfCancellationRequested();
            var reply = Execute(request.Command, bounds);
            await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumReply, token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Settings.Log($"分身桌面操作失败：{ex}");
            var reply = CommandReply.Failure(ex);
            if (pipe.IsConnected && !token.IsCancellationRequested)
            {
                try { await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumReply, token).ConfigureAwait(false); }
                catch (Exception writeError) { Settings.Log($"桌面助手回复失败：{writeError.Message}"); }
            }
            return reply.ExitCode;
        }
    }

    private static CommandReply Execute(DesktopCommand command, Rectangle bounds)
    {
        if (command.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("桌面操作参数必须是 JSON 对象。");
        if (command.Name == "take_screenshot")
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return new(true, "已截取分身桌面原始画面。",
                JsonSerializer.SerializeToElement(new { width = bounds.Width, height = bounds.Height }), output.ToArray());
        }
        if (command.Name is not ("mouse_move" or "mouse_click" or "mouse_scroll"))
            throw new ArgumentException("不支持的分身桌面操作。");
        var arguments = command.Arguments;
        var x = RequiredInt(arguments, "x");
        var y = RequiredInt(arguments, "y");
        if (x < 0 || x >= bounds.Width || y < 0 || y >= bounds.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"坐标需位于分身桌面范围 0..{bounds.Width - 1} × 0..{bounds.Height - 1}。");
        if (!SessionProcess.IsElevated((uint)Environment.ProcessId))
            throw new UnauthorizedAccessException("分身鼠标助手未获得管理员权限。");
        switch (command.Name)
        {
            case "mouse_move":
                DesktopInput.MoveMouse(bounds.Left + x, bounds.Top + y);
                break;
            case "mouse_click":
                var button = "left";
                if (arguments.TryGetProperty("button", out var buttonArg))
                {
                    if (buttonArg.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("参数 button 必须是字符串。");
                    button = buttonArg.GetString()!;
                }
                var count = arguments.TryGetProperty("count", out _) ? RequiredInt(arguments, "count") : 1;
                DesktopInput.ClickMouse(bounds.Left + x, bounds.Top + y, button, count);
                break;
            case "mouse_scroll":
                DesktopInput.Scroll(bounds.Left + x, bounds.Top + y, RequiredInt(arguments, "delta"));
                break;
        }
        return new(true, "已在分身桌面中发送鼠标事件。");
    }

    private static int RequiredInt(JsonElement arguments, string name)
    {
        if (arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
            return result;
        throw new ArgumentException($"参数 {name} 必须是整数。");
    }
}
