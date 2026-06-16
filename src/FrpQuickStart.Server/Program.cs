using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FrpQuickStart.Server;
using FrpQuickStart.Shared;

var configPath = GetOption(args, "--config") ?? Path.Combine(Environment.CurrentDirectory, "server-config.json");
if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return;
}

var settings = ServerSettings.LoadOrCreate(configPath);
var runtimeDir = Path.GetFullPath(settings.RuntimeDirectory);
Directory.CreateDirectory(runtimeDir);

Console.WriteLine("FRP QuickStart Ubuntu Server");
Console.WriteLine($"配置文件: {Path.GetFullPath(configPath)}");
Console.WriteLine($"控制服务: http://{settings.ControlBindAddress}:{settings.ControlPort}/");
Console.WriteLine($"frps 端口: {settings.FrpsBindPort}");
Console.WriteLine($"API 密钥: {settings.ApiSecret}");
if (string.IsNullOrWhiteSpace(settings.PublicAddress))
{
    Console.WriteLine("PublicAddress 未设置，将让 Windows 客户端使用它输入的服务器 IP。");
}
else
{
    Console.WriteLine($"公网地址: {settings.PublicAddress}");
}

Process? frpsProcess = null;
try
{
    frpsProcess = EnsureFrps(settings, runtimeDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

var listener = new TcpListener(GetBindAddress(settings.ControlBindAddress), settings.ControlPort);

try
{
    listener.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"控制服务启动失败: {ex.Message}");
    Console.Error.WriteLine("Ubuntu 上请确认端口未被占用；如使用 1024 以下端口，需要 root 权限。");
    return;
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    StopListener(listener);
    StopChild(frpsProcess);
};

Console.WriteLine("等待 Windows 客户端请求，按 Ctrl+C 退出。");

while (true)
{
    TcpClient client;
    try
    {
        client = await listener.AcceptTcpClientAsync();
    }
    catch (SocketException)
    {
        break;
    }
    catch (ObjectDisposedException)
    {
        break;
    }
    catch (InvalidOperationException)
    {
        break;
    }

    _ = Task.Run(() => HandleClientAsync(client, settings, runtimeDir, frpsProcess is not null));
}

StopChild(frpsProcess);

static Process? EnsureFrps(ServerSettings settings, string runtimeDir)
{
    if (IsTcpPortOpen(IPAddress.Loopback, settings.FrpsBindPort, TimeSpan.FromMilliseconds(250)))
    {
        Console.WriteLine("检测到 frps 端口已在监听，复用现有 frps。");
        return null;
    }

    var frpsConfigPath = Path.Combine(runtimeDir, "frps.toml");
    var frpsLogPath = Path.Combine(runtimeDir, "frps.log");
    FrpConfigWriter.WriteFrpsToml(frpsConfigPath, settings.FrpsBindPort, settings.FrpAuthToken, frpsLogPath);

    var frpsPath = ProcessHelpers.ResolveExecutable(settings.FrpsPath, "frps");
    Console.WriteLine($"启动 frps: {frpsPath} -c {frpsConfigPath}");
    return ProcessHelpers.StartFrp(frpsPath, frpsConfigPath);
}

static async Task HandleClientAsync(TcpClient client, ServerSettings settings, string runtimeDir, bool frpsStartedByServer)
{
    using (client)
    {
        var stream = client.GetStream();
        try
        {
            var request = await ReadHttpRequestAsync(stream);
            await HandleRequestAsync(stream, request, settings, runtimeDir, frpsStartedByServer);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stream, HttpStatusCode.InternalServerError, ex.Message);
        }
    }
}

static async Task HandleRequestAsync(Stream responseStream, SimpleHttpRequest httpRequest, ServerSettings settings, string runtimeDir, bool frpsStartedByServer)
{
    try
    {
        if (httpRequest.Method == "GET" && httpRequest.Path == "/health")
        {
            await WriteJsonAsync(responseStream, HttpStatusCode.OK, new HealthResponse
            {
                Success = true,
                Message = "ok",
                PublicAddress = settings.PublicAddress,
                FrpsBindPort = settings.FrpsBindPort,
                ControlPort = settings.ControlPort,
                FrpsStartedByServer = frpsStartedByServer
            }, FrpQuickJsonContext.Default.HealthResponse);
            return;
        }

        if (httpRequest.Method != "POST" || httpRequest.Path != "/api/tunnels")
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.NotFound, "接口不存在。可用接口: GET /health, POST /api/tunnels");
            return;
        }

        var request = JsonSerializer.Deserialize(httpRequest.Body, FrpQuickJsonContext.Default.TunnelRequest);
        if (request is null)
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.BadRequest, "请求 JSON 无效。");
            return;
        }

        var validationError = ValidateRequest(request, settings);
        if (validationError is not null)
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.BadRequest, validationError);
            return;
        }

        if (!IsRemotePortAvailable(request.Protocol, request.RemotePort))
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.Conflict, $"服务器端口 {request.RemotePort} 已被占用。");
            return;
        }

        var proxyName = FrpConfigWriter.SafeProxyName(request.ClientName, request.RemotePort);
        AppendTunnelRecord(runtimeDir, request, proxyName);

        Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} 接受隧道: {request.Protocol} :{request.RemotePort} -> {request.LocalIp}:{request.LocalPort} ({proxyName})");
        await WriteJsonAsync(responseStream, HttpStatusCode.OK, new TunnelResponse
        {
            Success = true,
            Message = "accepted",
            FrpServerAddress = settings.PublicAddress,
            FrpServerPort = settings.FrpsBindPort,
            Token = settings.FrpAuthToken,
            RemotePort = request.RemotePort,
            Protocol = request.Protocol,
            ProxyName = proxyName
        }, FrpQuickJsonContext.Default.TunnelResponse);
    }
    catch (Exception ex)
    {
        await WriteErrorAsync(responseStream, HttpStatusCode.InternalServerError, ex.Message);
    }
}

static string? ValidateRequest(TunnelRequest request, ServerSettings settings)
{
    if (!FixedTimeEquals(request.Secret, settings.ApiSecret))
    {
        return "密钥错误。";
    }

    if (!string.Equals(request.Protocol, "tcp", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(request.Protocol, "udp", StringComparison.OrdinalIgnoreCase))
    {
        return "Protocol 只能是 tcp 或 udp。";
    }

    request.Protocol = request.Protocol.ToLowerInvariant();

    if (!IsPort(request.LocalPort))
    {
        return "本地端口必须在 1-65535 之间。";
    }

    if (request.RemotePort < settings.AllowedRemotePortStart || request.RemotePort > settings.AllowedRemotePortEnd)
    {
        return $"服务器公网端口必须在 {settings.AllowedRemotePortStart}-{settings.AllowedRemotePortEnd} 之间。";
    }

    if (!IsValidHost(request.LocalIp))
    {
        return "本地监听 IP 无效。";
    }

    return null;
}

static bool IsRemotePortAvailable(string protocol, int port)
{
    if (string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            using var client = new UdpClient(port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    try
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

static bool IsTcpPortOpen(IPAddress address, int port, TimeSpan timeout)
{
    try
    {
        using var client = new TcpClient();
        var task = client.ConnectAsync(address, port);
        return task.Wait(timeout) && client.Connected;
    }
    catch
    {
        return false;
    }
}

static bool IsPort(int port) => port is >= 1 and <= 65535;

static bool IsValidHost(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    if (IPAddress.TryParse(value, out _))
    {
        return true;
    }

    return value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_');
}

static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static void AppendTunnelRecord(string runtimeDir, TunnelRequest request, string proxyName)
{
    var record = JsonSerializer.Serialize(new
    {
        Time = DateTimeOffset.UtcNow,
        request.Protocol,
        request.RemotePort,
        request.LocalIp,
        request.LocalPort,
        request.ClientName,
        ProxyName = proxyName
    });
    File.AppendAllText(Path.Combine(runtimeDir, "tunnels.jsonl"), record + Environment.NewLine, Encoding.UTF8);
}

static async Task WriteErrorAsync(Stream stream, HttpStatusCode statusCode, string message)
{
    await WriteJsonAsync(stream, statusCode, new TunnelResponse
    {
        Success = false,
        Message = message
    }, FrpQuickJsonContext.Default.TunnelResponse);
}

static async Task WriteJsonAsync<T>(Stream stream, HttpStatusCode statusCode, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
    var header = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {(int)statusCode} {ReasonPhrase(statusCode)}\r\n" +
        "Content-Type: application/json; charset=utf-8\r\n" +
        $"Content-Length: {body.Length}\r\n" +
        "Connection: close\r\n" +
        "\r\n");
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
}

static async Task<SimpleHttpRequest> ReadHttpRequestAsync(NetworkStream stream)
{
    var received = new List<byte>(4096);
    var buffer = new byte[4096];
    var headerEnd = -1;

    while (headerEnd < 0)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            throw new InvalidOperationException("HTTP 请求为空。");
        }

        received.AddRange(buffer.AsSpan(0, read).ToArray());
        headerEnd = IndexOfHeaderEnd(received);
        if (received.Count > 64 * 1024)
        {
            throw new InvalidOperationException("HTTP 请求头过大。");
        }
    }

    var allBytes = received.ToArray();
    var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
    var lines = headerText.Split("\r\n", StringSplitOptions.None);
    if (lines.Length == 0)
    {
        throw new InvalidOperationException("HTTP 请求行无效。");
    }

    var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (requestParts.Length < 2)
    {
        throw new InvalidOperationException("HTTP 请求行无效。");
    }

    var contentLength = 0;
    for (var i = 1; i < lines.Length; i++)
    {
        var separator = lines[i].IndexOf(':');
        if (separator <= 0)
        {
            continue;
        }

        var name = lines[i][..separator].Trim();
        var value = lines[i][(separator + 1)..].Trim();
        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out var parsed))
        {
            contentLength = parsed;
        }
    }

    if (contentLength < 0 || contentLength > 1024 * 1024)
    {
        throw new InvalidOperationException("HTTP 请求体大小无效。");
    }

    var body = new byte[contentLength];
    var bodyStart = headerEnd + 4;
    var bufferedBodyBytes = Math.Min(contentLength, allBytes.Length - bodyStart);
    if (bufferedBodyBytes > 0)
    {
        Array.Copy(allBytes, bodyStart, body, 0, bufferedBodyBytes);
    }

    var offset = bufferedBodyBytes;
    while (offset < contentLength)
    {
        var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset));
        if (read == 0)
        {
            throw new InvalidOperationException("HTTP 请求体未完整传输。");
        }

        offset += read;
    }

    var path = requestParts[1].Split('?', 2)[0];
    return new SimpleHttpRequest(requestParts[0].ToUpperInvariant(), path, body);
}

static int IndexOfHeaderEnd(List<byte> bytes)
{
    for (var i = 0; i <= bytes.Count - 4; i++)
    {
        if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
        {
            return i;
        }
    }

    return -1;
}

static IPAddress GetBindAddress(string bindAddress)
{
    if (bindAddress is "" or "0.0.0.0" or "*")
    {
        return IPAddress.Any;
    }

    if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Loopback;
    }

    if (IPAddress.TryParse(bindAddress, out var parsed))
    {
        return parsed;
    }

    var addresses = Dns.GetHostAddresses(bindAddress);
    return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ??
        addresses.FirstOrDefault() ??
        throw new InvalidOperationException($"无法解析控制服务监听地址: {bindAddress}");
}

static string ReasonPhrase(HttpStatusCode statusCode)
{
    return statusCode switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.Conflict => "Conflict",
        HttpStatusCode.InternalServerError => "Internal Server Error",
        _ => statusCode.ToString()
    };
}

static void StopListener(TcpListener listener)
{
    try
    {
        listener.Stop();
    }
    catch
    {
        // Listener may already be stopped.
    }
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static void StopChild(Process? process)
{
    if (process is null || process.HasExited)
    {
        return;
    }

    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch
    {
        // Process may have already exited.
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
    用法:
      frpquick-server [--config server-config.json]

    首次启动会生成 server-config.json，并打印 API 密钥。
    请把 frps 放到程序同目录，或安装到 PATH。
    """);
}

internal sealed record SimpleHttpRequest(string Method, string Path, byte[] Body);
