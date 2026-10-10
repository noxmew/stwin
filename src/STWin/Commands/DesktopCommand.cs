using System;
using System.ComponentModel;
using System.Security.Principal;
using System.Diagnostics;
using System.Text.Json;

namespace STWin.Commands;

internal sealed record DesktopCommand(string Name, JsonElement Arguments);
internal sealed record DesktopCommandResult(string Text, byte[]? ImagePng = null, JsonElement? Data = null);
internal sealed record CommandReply(bool Ok, string Message, JsonElement? Data = null, byte[]? ImagePng = null, string? Error = null, int ExitCode = 0)
{
    public static CommandReply Failure(Exception exception)
    {
        var error = exception.GetBaseException();
        var code = error switch
        {
            ArgumentException or FormatException => 2,
            TimeoutException => 4,
            UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => 5,
            OperationCanceledException => 130,
            InvalidOperationException => 3,
            _ => 1
        };
        return new(false, "", Error: error.Message, ExitCode: code);
    }
}

internal static class InstanceIdentity
{
    private static string Scope
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            using var process = Process.GetCurrentProcess();
            return $"STWin.{sid}.{process.SessionId}";
        }
    }
    public static string MutexName => $"Local\\{Scope}";
    public static string PipeName => $"{Scope}.commands";
}
