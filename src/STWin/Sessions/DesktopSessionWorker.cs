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

internal sealed record DesktopSessionRequest(uint SessionId, DesktopCommand Command, int Width, int Height)
{
    public void Validate()
    {
        if (SessionId is 0 or uint.MaxValue) throw new ArgumentException("分身会话 ID 无效。");
        if (Command is null || Command.Name is not ("take_screenshot" or "mouse_move" or "mouse_click" or "mouse_scroll" or "key_press"))
            throw new ArgumentException("不支持的分身桌面操作。");
        if (Command.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("桌面操作参数必须是 JSON 对象。");
        if (Width <= 0 || Height <= 0)
            throw new InvalidOperationException("尚未获取分身桌面的实际分辨率。");
    }
}

internal static class DesktopSessionWorker
{
    public const string Argument = "--desktop-worker";
    private const string PipePrefix = "STWin.desktop.";

    internal static async Task<CommandReply> ExecuteInParentSessionAsync(DesktopSessionRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var sessionId = request.SessionId;
        var command = request.Command;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var stage = "检查分身会话";
        try
        {
            if (!SessionProcess.IsElevated((uint)Environment.ProcessId))
                throw new UnauthorizedAccessException("分身桌面操作必须由管理员后台执行。");
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
            // The service's elevated parent-session helper owns the entire desktop operation.
            var launch = new ProgramLaunchRequest(sessionId, Environment.ProcessPath!,
                [Argument, sessionId.ToString(), name, Environment.ProcessId.ToString()], null, true);
            stage = "启动桌面助手";
            var launched = await ProgramLauncher.LaunchInSessionAsync(launch, token).ConfigureAwait(false);
            stage = "打开桌面助手进程";
            using var worker = SessionProcess.OpenForInspection(checked((uint)launched.ProcessId));
            stage = "等待桌面助手连接";
            await SessionProcess.WaitForConnectionAsync(pipe, worker, token).ConfigureAwait(false);
            stage = "验证桌面助手身份";
            var workerId = SessionProcess.PipeClientProcessId(pipe);
            if (workerId != launched.ProcessId || SessionProcess.SessionId(workerId) != sessionId ||
                SessionProcess.UserSid(workerId) != sid.Value ||
                !string.Equals(SessionProcess.ImagePath(workerId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                !SessionProcess.IsElevated(workerId))
                throw new UnauthorizedAccessException("分身桌面助手身份不匹配。");
            if (SessionApi.ChildId() != sessionId) throw new InvalidOperationException("分身会话已变化，已取消操作。");
            stage = "发送桌面指令";
            await CommandPipe.WriteAsync(pipe, request, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            stage = "接收桌面操作结果";
            var reply = await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumReply, token).ConfigureAwait(false);
            var exitCode = await SessionProcess.WaitForExitAsync(worker, token).ConfigureAwait(false);
            if (!reply.Ok) throw new CommandFailedException(reply.Error ?? "分身桌面操作失败。", reply.ExitCode);
            if (exitCode != 0) throw new InvalidOperationException("分身桌面助手没有正常退出，请检查分身状态。");
            if (SessionApi.ChildId() != sessionId) throw new InvalidOperationException("操作期间分身会话已变化。");
            return reply;
        }
        catch (Exception ex)
        {
            Settings.Log($"分身桌面命令 {command.Name} 失败（会话 {sessionId}，阶段：{stage}）：{ex}");
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("分身桌面操作超时，请查询状态后再操作。", ex);
            throw;
        }
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
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            if (SessionProcess.PipeServerProcessId(pipe) != parentId)
                throw new UnauthorizedAccessException("桌面助手连接到了错误的 STWin 进程。");
            if (SessionProcess.SessionId((uint)Environment.ProcessId) != sessionId)
                throw new UnauthorizedAccessException("桌面助手不在指定分身会话中。");
            if (!SessionProcess.IsElevated((uint)Environment.ProcessId))
                throw new UnauthorizedAccessException("分身桌面助手未获得管理员权限。");
            using var identity = WindowsIdentity.GetCurrent();
            var parentSession = SessionProcess.SessionId(parentId);
            if (parentSession == 0 || parentSession == sessionId ||
                SessionProcess.UserSid(parentId) != identity.User?.Value ||
                !SessionProcess.IsElevated(parentId) ||
                !string.Equals(SessionProcess.ImagePath(parentId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("桌面助手没有连接到原登录会话中的管理员后台。");
            var request = await CommandPipe.ReadAsync<DesktopSessionRequest>(pipe, CommandPipe.MaximumRequest, token).ConfigureAwait(false);
            request.Validate();
            if (request.SessionId != sessionId) throw new InvalidOperationException("桌面操作请求的分身会话不匹配。");
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
        if (command.Name is not ("mouse_move" or "mouse_click" or "mouse_scroll" or "key_press"))
            throw new ArgumentException("不支持的分身桌面操作。");
        var arguments = command.Arguments;
        if (command.Name == "key_press")
        {
            if (!arguments.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                throw new ArgumentException("参数 key 必须是字符串。");
            DesktopInput.PressKey(DesktopInput.ParseKey(key.GetString()!), ReadModifiers(arguments));
            return new(true, "已在分身桌面中发送键盘事件。");
        }
        var x = RequiredInt(arguments, "x");
        var y = RequiredInt(arguments, "y");
        if (x < 0 || x >= bounds.Width || y < 0 || y >= bounds.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"坐标需位于分身桌面范围 0..{bounds.Width - 1} × 0..{bounds.Height - 1}。");
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

    private static Keys[] ReadModifiers(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("modifiers", out var value)) return Array.Empty<Keys>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4)
            throw new ArgumentException("参数 modifiers 必须是最多包含 4 项的数组。");
        var keys = new Keys[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("修饰键名称必须是字符串。");
            var modifier = DesktopInput.ParseKey(item.GetString()!);
            if (modifier is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin))
                throw new ArgumentException("modifiers 仅支持 CTRL、SHIFT、ALT 或 WIN。");
            keys[index++] = modifier;
        }
        return keys;
    }

    private static int RequiredInt(JsonElement arguments, string name)
    {
        if (arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
            return result;
        throw new ArgumentException($"参数 {name} 必须是整数。");
    }
}
