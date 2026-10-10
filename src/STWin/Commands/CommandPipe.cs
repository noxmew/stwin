using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STWin.Infrastructure;

namespace STWin.Commands;

internal static class CommandPipe
{
    public const int MaximumRequest = 128 * 1024;
    public const int MaximumReply = 256 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task WriteAsync<T>(Stream stream, T message, int maximum, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > maximum) throw new InvalidDataException("CLI 消息超过大小限制。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int maximum, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximum) throw new InvalidDataException("CLI 消息长度无效。");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new InvalidDataException("CLI 消息为空。");
    }
}

internal sealed class CommandServer(Func<DesktopCommand, CancellationToken, Task<DesktopCommandResult>> execute, Action quit) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connections = new(3, 3);
    private NamedPipeServerStream? listener;

    public void Start()
    {
        listener = NewListener();
        _ = AcceptAsync();
    }

    private static NamedPipeServerStream NewListener() => new(InstanceIdentity.PipeName, PipeDirection.InOut,
        4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await connections.WaitAsync(lifetime.Token).ConfigureAwait(false);
                var pipe = listener!;
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    listener = NewListener();
                }
                catch
                {
                    connections.Release();
                    pipe.Dispose();
                    throw;
                }
                _ = HandleAsync(pipe);
            }
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested && (ex is OperationCanceledException or ObjectDisposedException or IOException)) { }
        catch (Exception ex) { Settings.Log($"CLI 服务停止：{ex}"); }
        finally { listener?.Dispose(); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        var shouldQuit = false;
        try
        {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(75));
                try
                {
                    var command = await CommandPipe.ReadAsync<DesktopCommand>(pipe, CommandPipe.MaximumRequest, timeout.Token).ConfigureAwait(false);
                    if (command.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("命令参数必须是 JSON 对象。");
                    CommandReply reply;
                    var disconnected = pipe.ReadAsync(new byte[1], timeout.Token).AsTask();
                    var action = execute(command, timeout.Token);
                    try
                    {
                        if (await Task.WhenAny(action, disconnected).ConfigureAwait(false) == disconnected)
                            timeout.Cancel();
                        var result = await action.ConfigureAwait(false);
                        reply = new(true, result.Text, result.Data, result.ImagePng);
                        shouldQuit = command.Name == "quit";
                    }
                    catch (Exception ex) { reply = CommandReply.Failure(ex); }
                    await CommandPipe.WriteAsync(pipe, reply, CommandPipe.MaximumReply, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Settings.Log($"CLI 请求结束：{ex.Message}"); }
                finally { timeout.Cancel(); }
            }
        }
        finally
        {
            connections.Release();
            if (shouldQuit) quit();
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener?.Dispose();
    }
}
