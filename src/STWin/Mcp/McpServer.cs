using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using STWin.Infrastructure;
using STWin.Commands;
using STWin.Updates;

namespace STWin.Mcp;

internal sealed class McpServer : IDisposable
{
    private const string ProtocolVersion = "2025-03-26";
    private const int FirstPort = 51374;
    private const int PortCount = 12;
    private const int MaxRequestBytes = 1_000_000;
    private readonly Func<string, JsonElement, Task<DesktopCommandResult>> callTool;
    private readonly CancellationTokenSource lifetime = new();
    private TcpListener? listener;
    private int port;

    public McpServer(Func<string, JsonElement, Task<DesktopCommandResult>> callTool)
    {
        this.callTool = callTool;
    }

    public string Endpoint => $"http://127.0.0.1:{port}/mcp";

    public void Start()
    {
        if (listener is not null) return;
        Exception? lastError = null;
        for (var candidate = FirstPort; candidate < FirstPort + PortCount; candidate++)
        {
            var attempt = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                attempt.Start(16);
                listener = attempt;
                port = candidate;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                attempt.Stop();
            }
        }
        if (listener is null) throw new InvalidOperationException("无法为本机 MCP 服务分配端口。", lastError);
        WriteClientConfig();
        _ = ServeAsync(lifetime.Token);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try { listener?.Stop(); }
        catch (SocketException) { }
        listener = null;
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string? response = null;
            Action? afterReply = null;
            var status = "200 OK";
            var protocol = ProtocolVersion;
            try
            {
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                protocol = request.Headers.TryGetValue("mcp-protocol-version", out var requestedVersion) && IsSupportedVersion(requestedVersion)
                    ? requestedVersion : ProtocolVersion;
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    status = "405 Method Not Allowed";
                    response = ErrorBody("Only MCP POST requests are supported.");
                }
                else if (request.Path != "/mcp")
                {
                    status = "404 Not Found";
                    response = ErrorBody("MCP endpoint not found.");
                }
                else if (!request.Headers.TryGetValue("content-type", out var contentType) || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    status = "415 Unsupported Media Type";
                    response = ErrorBody("Content-Type must be application/json.");
                }
                else
                {
                    var message = await ProcessMessageAsync(request.Body, cancellationToken).ConfigureAwait(false);
                    response = message.Body;
                    afterReply = message.AfterReply;
                }
            }
            catch (InvalidDataException ex)
            {
                status = "400 Bad Request";
                response = ErrorBody(ex.Message);
            }
            catch (JsonException ex)
            {
                status = "400 Bad Request";
                response = ErrorBody($"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Settings.Log($"MCP 请求失败：{ex}");
                status = "500 Internal Server Error";
                response = ErrorBody("The MCP server could not process this request.");
            }

            try
            {
                if (response is null)
                {
                    await WriteResponseAsync(stream, "202 Accepted", string.Empty, protocol, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WriteResponseAsync(stream, status, response, protocol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { afterReply?.Invoke(); }
        }
    }

    private sealed record McpReply(string? Body, Action? AfterReply = null);

    private async Task<McpReply> ProcessMessageAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MCP request must be a JSON object.");
        if (!root.TryGetProperty("jsonrpc", out var jsonrpc) || jsonrpc.GetString() != "2.0")
            throw new InvalidDataException("jsonrpc must be \"2.0\".");
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("MCP request must include a method.");

        var method = methodElement.GetString()!;
        var hasId = root.TryGetProperty("id", out var id);
        var idJson = hasId ? id.GetRawText() : "null";
        if (!hasId)
        {
            return new(null);
        }

        object result;
        Action? afterReply = null;
        switch (method)
        {
            case "initialize":
                var version = ProtocolVersion;
                if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("protocolVersion", out var clientVersion) && clientVersion.ValueKind == JsonValueKind.String && IsSupportedVersion(clientVersion.GetString()!))
                    version = clientVersion.GetString()!;
                result = new
                {
                    protocolVersion = version,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "STWin", version = UpdateRelease.CurrentVersionText },
                    instructions = "Control the connected STWin desktop clone. Mouse coordinates use pixels from the latest screenshot. run_script submits PowerShell in the clone's interactive desktop."
                };
                break;
            case "ping":
                result = new { };
                break;
            case "tools/list":
                result = new { tools = ToolDefinitions };
                break;
            case "tools/call":
                if (!root.TryGetProperty("params", out var callParams) || callParams.ValueKind != JsonValueKind.Object ||
                    !callParams.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                    return new(JsonRpcError(idJson, -32602, "tools/call requires params.name."));
                var name = nameElement.GetString()!;
                var arguments = callParams.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                    ? args.Clone() : EmptyObject();
                try
                {
                    var toolResult = await callTool(name, arguments).ConfigureAwait(false);
                    afterReply = toolResult.AfterReply;
                    var text = toolResult.Data is { } data ? JsonSerializer.Serialize(data) : toolResult.Text;
                    var content = new List<object> { new { type = "text", text } };
                    if (toolResult.ImagePng is { Length: > 0 } image)
                        content.Add(new { type = "image", data = Convert.ToBase64String(image), mimeType = "image/png" });
                    result = new { content, isError = false };
                }
                catch (Exception ex)
                {
                    result = new { content = new[] { new { type = "text", text = ex.GetBaseException().Message } }, isError = true };
                }
                break;
            default:
                return new(JsonRpcError(idJson, -32601, $"Unknown MCP method: {method}"));
        }
        return new($"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{JsonSerializer.Serialize(result)}}}", afterReply);
    }

    private static readonly object[] ToolDefinitions =
    {
        new
        {
            name = "update_program",
            description = "Check for a newer STWin release. Report no new version, or download and verify the update and restart STWin automatically. Apps in the clone are preserved; reconnect MCP after restart.",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = true }
        },
        new
        {
            name = "get_desktop_status",
            description = "Report whether the STWin clone is connected and its desktop resolution.",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "take_screenshot",
            description = "Capture the RDP clone surface directly and return a PNG image without activating STWin.",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "mouse_move",
            description = "Send a background mouse-move event to pixel coordinates from the most recent screenshot.",
            inputSchema = new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" } }, required = new[] { "x", "y" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "mouse_click",
            description = "Send background mouse-button events at pixel coordinates from the most recent screenshot.",
            inputSchema = new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" }, button = new { type = "string", @enum = new[] { "left", "right", "middle" } }, count = new { type = "integer", minimum = 1, maximum = 2 } }, required = new[] { "x", "y" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "mouse_scroll",
            description = "Send a background mouse-wheel event at pixel coordinates from the most recent screenshot. delta is commonly 120 or -120.",
            inputSchema = new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" }, delta = new { type = "integer", minimum = -1200, maximum = 1200 } }, required = new[] { "x", "y", "delta" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "key_press",
            description = "Send a named key directly to the RDP session, optionally with CTRL, ALT, SHIFT, or WIN; it does not require STWin to be focused.",
            inputSchema = new { type = "object", properties = new { key = new { type = "string" }, modifiers = new { type = "array", items = new { type = "string" }, maxItems = 4 } }, required = new[] { "key" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "type_text",
            description = "Paste Unicode text into the clone through redirected RDP clipboard without focusing STWin. The local clipboard is restored when possible.",
            inputSchema = new { type = "object", properties = new { text = new { type = "string", maxLength = 10000 } }, required = new[] { "text" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false }
        },
        new
        {
            name = "run_script",
            description = "Open PowerShell inside the connected clone through RDP key events, submit the supplied script through redirected clipboard, and return a background screenshot. STWin does not need focus. The script runs as the clone's signed-in user and can change files or system settings there.",
            inputSchema = new { type = "object", properties = new { script = new { type = "string", minLength = 1, maxLength = 4000 } }, required = new[] { "script" }, additionalProperties = false },
            annotations = new { readOnlyHint = false, destructiveHint = true, openWorldHint = true }
        }
    };

    private static bool IsSupportedVersion(string value)
        => value is "2025-03-26" or "2024-11-05";

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string JsonRpcError(string idJson, int code, string message)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}}}}}";

    private static string ErrorBody(string message)
        => JsonSerializer.Serialize(new { error = message });

    private async Task WriteResponseAsync(NetworkStream stream, string status, string body, string protocol, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nMCP-Protocol-Version: {protocol}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(1024);
        var one = new byte[1];
        while (header.Count < 32_768)
        {
            var read = await stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Connection closed before the HTTP header completed.");
            header.Add(one[0]);
            var count = header.Count;
            if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
        }
        if (header.Count >= 32_768) throw new InvalidDataException("HTTP headers are too large.");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new InvalidDataException("Invalid HTTP request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) break;
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("Invalid HTTP header.");
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }
        if (!headers.TryGetValue("content-length", out var contentLengthText) || !int.TryParse(contentLengthText, out var contentLength) || contentLength < 0 || contentLength > MaxRequestBytes)
            throw new InvalidDataException("A valid Content-Length up to 1 MB is required.");
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Connection closed before the HTTP body completed.");
            offset += read;
        }
        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0], path, headers, Encoding.UTF8.GetString(body));
    }

    private void WriteClientConfig()
    {
        Directory.CreateDirectory(Settings.Folder);
        var path = Path.Combine(Settings.Folder, "mcp-connection.json");
        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(new
        {
            transport = "streamable-http",
            url = Endpoint
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);
}
