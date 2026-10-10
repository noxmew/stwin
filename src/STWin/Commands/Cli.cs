using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Updates;
using STWin.Windows;

namespace STWin.Commands;

internal sealed record CliInvocation(DesktopCommand? Command, bool Json, string? OutputPath = null, bool Help = false, bool Version = false);

internal static class Cli
{
    private const string HelpText = """
        STWin 命令行
        STWin.exe <命令> [--json]

        status                            查询运行状态、会话 ID 和连接信息
        start                             创建并连接桌面；已有会话时请用 connect
        connect [--session ID]            连接已有桌面，可指定会话 ID
        disconnect                        断开连接，保留分身中的程序
        stop --yes [--session ID]         结束桌面分身，关闭其中的程序
        quit                              断开连接并退出 STWin，保留分身
        show / hide                       显示 / 隐藏主窗口
        fullscreen on|off                 开启 / 关闭全屏
        settings                          查询设置
        settings set NAME true|false      修改 topmost、smart-sizing、muted、remote-keys
        screenshot FILE.png               保存截图；文件不能已存在
        mouse move X Y                    移动鼠标
        mouse click X Y [BUTTON]          点击；BUTTON 为 left、right、middle
        mouse click X Y BUTTON --double   双击
        mouse scroll X Y DELTA            滚轮，DELTA 介于 -1200 至 1200
        key CTRL+SHIFT+A                  按键或组合键
        text [--] "内容"                  输入文本；-- 后的内容按原文处理
        text --file FILE.txt              从本机 UTF-8 文件读取文本
        script FILE.ps1 [--output PNG]    向分身 PowerShell 提交脚本，可保存截图
        launch PROGRAM [选项] [-- ARG...] 在分身内启动程序，返回进程 ID、路径和会话 ID
          --cwd DIR / --session ID       指定工作目录 / 核对分身会话 ID；程序参数放在 -- 后
        update                            检查更新；无新版本时提示，有新版本时升级并重启
        version / --version              查看版本
        help / --help                    查看帮助

        --json 返回 JSON；成功退出码为 0，失败为 1，参数错误为 2，状态冲突为 3，
        超时为 4，权限不足为 5，取消为 130。script 返回成功表示脚本已提交。
        start、connect、show、stop、settings、update 在需要时启动 STWin。
        命令作用于当前用户和登录会话；管理员操作需从管理员终端启动 STWin。
        交互式 PowerShell 请用 STWin.exe status | Out-Host 等待命令结束；
        CMD 使用 start /wait "" STWin.exe status。脚本或 PowerShell 管道可读取输出和退出码。
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        CliConsole.Attach();
        var json = JsonRequested(args);
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var invocation = Parse(args);
            if (invocation.Help) { Console.WriteLine(HelpText); return 0; }
            if (invocation.Version)
            {
                Console.WriteLine(invocation.Json ? JsonSerializer.Serialize(new { version = UpdateRelease.CurrentVersionText }) : $"STWin {UpdateRelease.CurrentVersionText}");
                return 0;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
            deadline.CancelAfter(invocation.Command!.Name == "update_program" ? TimeSpan.FromMinutes(16) : TimeSpan.FromSeconds(90));
            CommandReply reply;
            try { reply = await SendAsync(invocation.Command!, deadline.Token); }
            catch (OperationCanceledException) when (!cancelled.IsCancellationRequested)
            { throw new TimeoutException("命令超时。请用 status 查询当前状态，不要自动重复提交操作。"); }
            if (reply.Ok && invocation.OutputPath is not null)
            {
                if (reply.ImagePng is not { Length: > 0 } image) throw new InvalidDataException("命令没有返回 PNG 截图。");
                await using var file = new FileStream(invocation.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await file.WriteAsync(image, cancelled.Token);
                reply = reply with { Data = JsonSerializer.SerializeToElement(new { path = invocation.OutputPath, bytes = image.Length }), ImagePng = null };
            }
            WriteReply(reply, invocation.Json);
            return reply.ExitCode;
        }
        catch (Exception ex)
        {
            var reply = CommandReply.Failure(ex);
            WriteReply(reply, json);
            return reply.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static void WriteReply(CommandReply reply, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(reply with { ImagePng = null }, CommandPipe.JsonOptions));
            return;
        }
        if (!reply.Ok) { Console.Error.WriteLine(reply.Error); return; }
        if (reply.Data is { } data)
            Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine(reply.Message);
    }

    private static async Task<CommandReply> SendAsync(DesktopCommand command, CancellationToken token)
    {
        var running = Mutex.TryOpenExisting(InstanceIdentity.MutexName, out var existing);
        existing?.Dispose();
        if (!running)
        {
            if (command.Name == "get_desktop_status")
                return new(true, "STWin 未运行。", DesktopStatus.NotRunning());
            if (command.Name == "quit") return new(true, "STWin 未运行。");
            if (command.Name is not ("start_desktop" or "connect_desktop" or "show" or "stop_desktop" or "get_settings" or "set_settings" or "update_program"))
                throw new InvalidOperationException("STWin 未运行。请先执行 start 或 connect。");
            // Shell launch keeps the resident GUI from inheriting the CLI's output pipe.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true };
            start.ArgumentList.Add("--control-host");
            using var host = Process.Start(start) ?? throw new InvalidOperationException("无法启动 STWin。");
        }
        using var pipe = new NamedPipeClientStream(".", InstanceIdentity.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await pipe.ConnectAsync(connectTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("无法连接 STWin CLI 服务，请确认当前登录会话中的 STWin 已启动。"); }
        }
        await CommandPipe.WriteAsync(pipe, command, CommandPipe.MaximumRequest, token);
        return await CommandPipe.ReadAsync<CommandReply>(pipe, CommandPipe.MaximumReply, token);
    }

    private static CliInvocation Parse(string[] arguments)
    {
        var optionCount = arguments.TakeWhile(item => item != "--").Count();
        var flags = arguments[..optionCount].Count(item => item == "--json");
        if (flags > 1) throw new ArgumentException("--json 不能重复指定。");
        var json = flags == 1;
        var values = arguments.Where((item, index) => index >= optionCount || item != "--json").ToList();
        var args = values.ToArray();
        switch (args)
        {
            case ["help" or "--help" or "-h"]: return new(null, json, Help: true);
            case ["version" or "--version" or "-v"]: return new(null, json, Version: true);
            case ["update"]: return Call("update_program", new { });
            case ["launch", var program, .. var options]:
                string? directory = null;
                uint? launchSession = null;
                var programArgs = Array.Empty<string>();
                for (var index = 0; index < options.Length; index++)
                {
                    if (options[index] == "--") { programArgs = options[(index + 1)..]; break; }
                    if (options[index] == "--cwd" && directory is null && index + 1 < options.Length)
                        directory = Path.GetFullPath(options[++index]);
                    else if (options[index] == "--session" && launchSession is null && index + 1 < options.Length)
                        launchSession = SessionId(options[++index]);
                    else throw new ArgumentException("launch 选项为 --cwd DIR、--session ID；程序参数请放在 -- 后。");
                }
                var fileName = program.IndexOfAny(['\\', '/', ':']) >= 0 ? Path.GetFullPath(program) : program;
                var launch = new Dictionary<string, object> { ["fileName"] = fileName, ["arguments"] = programArgs };
                if (directory is not null) launch["workingDirectory"] = directory;
                if (launchSession is not null) launch["sessionId"] = launchSession.Value;
                return Call("launch_program", launch);
            case ["status"]: return Call("get_desktop_status", new { });
            case ["start"]: return Call("start_desktop", new { });
            case ["connect"]: return Call("connect_desktop", new { });
            case ["connect", "--session", var session]: return Call("connect_desktop", new { sessionId = SessionId(session) });
            case ["disconnect"]: return Call("disconnect_desktop", new { });
            case ["stop", "--yes"]: return Call("stop_desktop", new { confirm = true });
            case ["stop", "--yes", "--session", var session]: return Call("stop_desktop", new { confirm = true, sessionId = SessionId(session) });
            case ["stop", "--session", var session, "--yes"]: return Call("stop_desktop", new { confirm = true, sessionId = SessionId(session) });
            case ["stop", ..]: throw new ArgumentException("结束分身会关闭其中的程序，请显式使用 stop --yes [--session ID]。");
            case ["quit" or "show" or "hide"]: return Call(args[0], new { });
            case ["fullscreen", "on" or "off"]: return Call("set_fullscreen", new { enabled = args[1] == "on" });
            case ["settings"]: return Call("get_settings", new { });
            case ["settings", "set", var name, var value]:
                var key = name switch
                {
                    "topmost" => "topmost", "smart-sizing" => "smartSizing", "muted" => "muted", "remote-keys" => "remoteKeys",
                    _ => throw new ArgumentException("设置名称只能是 topmost、smart-sizing、muted 或 remote-keys。")
                };
                if (!bool.TryParse(value, out var enabled)) throw new ArgumentException("设置值必须是 true 或 false。");
                return Call("set_settings", new Dictionary<string, bool> { [key] = enabled });
            case ["screenshot", var path]: return Call("take_screenshot", new { }, path);
            case ["mouse", "move", var x, var y]: return Call("mouse_move", new { x = Number(x), y = Number(y) });
            case ["mouse", "click", var x, var y]: return Click(x, y, "left", 1);
            case ["mouse", "click", var x, var y, var button]: return Click(x, y, button, 1);
            case ["mouse", "click", var x, var y, var button, "--double"]: return Click(x, y, button, 2);
            case ["mouse", "scroll", var x, var y, var delta]:
                var amount = Number(delta);
                if (amount is < -1200 or > 1200) throw new ArgumentException("滚轮值需介于 -1200 至 1200。");
                return Call("mouse_scroll", new { x = Number(x), y = Number(y), delta = amount });
            case ["key", var chord]:
                var parts = chord.Split('+', StringSplitOptions.TrimEntries);
                if (parts.Length is < 1 or > 5 || parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("组合键格式为 CTRL+SHIFT+A，最多 4 个修饰键。");
                _ = DesktopInput.ParseKey(parts[^1]);
                var modifiers = parts[..^1];
                if (modifiers.Any(item => item.ToUpperInvariant() is not ("CTRL" or "SHIFT" or "ALT" or "WIN")))
                    throw new ArgumentException("修饰键只能是 CTRL、SHIFT、ALT 或 WIN。");
                return Call("key_press", new { key = parts[^1], modifiers });
            case ["text", "--file"]: throw new ArgumentException("text --file 需要指定本机文件路径。");
            case ["text", var text]: return Text(text);
            case ["text", "--", var text]: return Text(text);
            case ["text", "--file", var file]: return Text(ReadFile(file, 10000));
            case ["script", var file]: return Script(file, null);
            case ["script", var file, "--output", var path]: return Script(file, path);
            default: throw new ArgumentException("命令或参数无效。使用 STWin.exe help 查看命令。");
        }

        CliInvocation Call(string name, object payload, string? output = null)
        {
            var path = output is null ? null : Path.GetFullPath(output);
            if (path is not null && File.Exists(path)) throw new ArgumentException($"输出文件已存在：{path}");
            return new(new(name, JsonSerializer.SerializeToElement(payload)), json, path);
        }
        CliInvocation Click(string x, string y, string button, int count)
        {
            if (button is not ("left" or "right" or "middle")) throw new ArgumentException("鼠标按键只能是 left、right 或 middle。");
            return Call("mouse_click", new { x = Number(x), y = Number(y), button, count });
        }
        CliInvocation Text(string text)
        {
            if (text.Length > 10000) throw new ArgumentException("一次最多输入 10000 个字符。");
            return Call("type_text", new { text });
        }
        CliInvocation Script(string file, string? output)
        {
            var script = ReadFile(file, 4000);
            if (script.Length is < 1 or > 4000) throw new ArgumentException("脚本长度需介于 1 至 4000 个字符。");
            return Call("run_script", new { script }, output);
        }
    }

    private static bool JsonRequested(string[] args) => args.TakeWhile(item => item != "--").Contains("--json");

    private static string ReadFile(string path, int maximumCharacters)
    {
        if (new FileInfo(path).Length > maximumCharacters * 4L + 4) throw new ArgumentException("输入文件超过命令的长度限制。");
        return File.ReadAllText(path);
    }

    private static int Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? number : throw new ArgumentException($"需要整数：{value}");

    private static uint SessionId(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != uint.MaxValue
        ? id : throw new ArgumentException($"会话 ID 无效：{value}");
}

internal static class DesktopStatus
{
    public static JsonElement NotRunning() => JsonSerializer.SerializeToElement(new
    {
        version = UpdateRelease.CurrentVersionText, running = false, processId = (int?)null,
        childSessionsEnabled = SessionApi.IsEnabled(), sessionId = SessionApi.ChildId(), managedSessionId = (uint?)null,
        connected = false, connectionState = 0, busy = false, operation = "none",
        resolution = (object?)null, fullscreen = false, mcpEndpoint = (string?)null
    });
}
