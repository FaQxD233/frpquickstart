using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FrpQuickStart.Shared;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return;
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("FRP QuickStart Windows Client");
Console.WriteLine("请按提示填写 Ubuntu 服务器和本地服务信息。");
Console.WriteLine();

var serverHost = GetOption(args, "--server") ?? PromptRequired("Ubuntu 控制服务 IP/域名");
var controlPort = GetIntOption(args, "--control-port") ?? PromptPort("Ubuntu 控制服务端口", 9080);
var remotePort = GetIntOptionRequired(args, "--remote-port") ?? PromptPort("要开放在服务器上的公网端口", null);
var localIp = GetOption(args, "--local-ip") ?? PromptWithDefault("本地监听 IP", "127.0.0.1");
var localPort = GetIntOptionRequired(args, "--local-port") ?? PromptPort("本地监听端口", null);
var secret = GetOption(args, "--secret") ?? PromptSecret("连接密钥");
var protocol = (GetOption(args, "--protocol") ?? "tcp").Trim().ToLowerInvariant();
var bundledFrpcResource = OperatingSystem.IsWindows() ? "FrpQuickStart.Bundled.frpc.exe" : null;
var frpcPath = ProcessHelpers.ResolveExecutable(GetOption(args, "--frpc"), "frpc", bundledFrpcResource);

if (protocol is not "tcp" and not "udp")
{
    Console.Error.WriteLine("协议只能是 tcp 或 udp。");
    return;
}

var controlUrl = BuildControlUrl(serverHost, controlPort);
var clientName = Environment.MachineName;
var request = new TunnelRequest
{
    Secret = secret,
    RemotePort = remotePort,
    LocalIp = localIp,
    LocalPort = localPort,
    Protocol = protocol,
    ClientName = clientName
};

TunnelResponse? response;
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var requestJson = JsonSerializer.Serialize(request, FrpQuickJsonContext.Default.TunnelRequest);
    using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
    var httpResponse = await http.PostAsync($"{controlUrl}/api/tunnels", content);
    response = await httpResponse.Content.ReadFromJsonAsync(FrpQuickJsonContext.Default.TunnelResponse);

    if (response is null)
    {
        Console.Error.WriteLine("服务器返回内容无效。");
        return;
    }

    if (!httpResponse.IsSuccessStatusCode || !response.Success)
    {
        Console.Error.WriteLine($"服务器拒绝请求: {response.Message}");
        return;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"无法连接 Ubuntu 控制服务 {controlUrl}: {ex.Message}");
    return;
}

var frpServerAddress = string.IsNullOrWhiteSpace(response.FrpServerAddress)
    ? serverHost
    : response.FrpServerAddress;
var proxyName = string.IsNullOrWhiteSpace(response.ProxyName)
    ? FrpConfigWriter.SafeProxyName(clientName, remotePort, protocol)
    : response.ProxyName;
var runtimeDir = Path.Combine(Environment.CurrentDirectory, "runtime");
var frpcConfigPath = Path.Combine(runtimeDir, $"frpc-{remotePort}.toml");

FrpConfigWriter.WriteFrpcToml(
    frpcConfigPath,
    frpServerAddress,
    response.FrpServerPort,
    response.Token,
    proxyName,
    response.Protocol,
    localIp,
    localPort,
    response.RemotePort);

Console.WriteLine();
Console.WriteLine("隧道配置已生成:");
Console.WriteLine($"  {frpcConfigPath}");
Console.WriteLine($"公网访问地址: {frpServerAddress}:{response.RemotePort}");
Console.WriteLine("正在启动 frpc，保持此窗口打开即可保持穿透在线。按 Ctrl+C 停止。");

Process? frpcProcess;
try
{
    frpcProcess = ProcessHelpers.StartFrp(frpcPath, frpcConfigPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    StopChild(frpcProcess);
};

await frpcProcess.WaitForExitAsync();
Console.WriteLine($"frpc 已退出，退出码: {frpcProcess.ExitCode}");
Environment.ExitCode = frpcProcess.ExitCode;

static string BuildControlUrl(string hostOrUrl, int port)
{
    if (Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        return uri.ToString().TrimEnd('/');
    }

    return $"http://{hostOrUrl}:{port}";
}

static string PromptRequired(string label)
{
    while (true)
    {
        Console.Write($"{label}: ");
        var input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(input))
        {
            return input;
        }
    }
}

static string PromptWithDefault(string label, string defaultValue)
{
    Console.Write($"{label} [{defaultValue}]: ");
    var input = Console.ReadLine()?.Trim();
    return string.IsNullOrWhiteSpace(input) ? defaultValue : input;
}

static int PromptPort(string label, int? defaultValue)
{
    while (true)
    {
        Console.Write(defaultValue is null ? $"{label}: " : $"{label} [{defaultValue}]: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input) && defaultValue is not null)
        {
            return defaultValue.Value;
        }

        if (int.TryParse(input, out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        Console.WriteLine("请输入 1-65535 之间的端口号。");
    }
}

static string PromptSecret(string label)
{
    Console.Write($"{label}: ");
    var secret = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        // QUAL-4 FIX: 退格键回显删除效果
        if (key.Key == ConsoleKey.Backspace)
        {
            if (secret.Length > 0)
            {
                secret.Length--;
                Console.Write("\b \b"); // 回退、空格覆盖、再回退
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            secret.Append(key.KeyChar);
        }
    }

    return secret.ToString();
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

/// <summary>
/// 可选的整数参数解析，参数不存在时返回 null（进入交互提示）。
/// </summary>
static int? GetIntOption(string[] args, string name)
{
    var value = GetOption(args, name);
    if (value is null)
    {
        return null;
    }

    return int.TryParse(value, out var parsed) ? parsed : null;
}

/// <summary>
/// QUAL-3 FIX: 必需的整数参数解析，参数存在但无法解析为数字时 print error and exit。
/// 用于 --remote-port 和 --local-port 这类必需参数。
/// </summary>
static int? GetIntOptionRequired(string[] args, string name)
{
    // 检查参数是否存在于命令行
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            var value = args[i + 1];
            if (int.TryParse(value, out var parsed) && parsed is >= 1 and <= 65535)
            {
                return parsed;
            }

            Console.Error.WriteLine($"错误: {name} 的值 '{value}' 不是有效的端口号 (1-65535)。");
            Environment.ExitCode = 1;
            return null;
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
      frpquick-client [选项]

    常用选项:
      --server <ip或域名>          Ubuntu 控制服务 IP/域名
      --control-port <端口>       Ubuntu 控制服务端口，默认 9080
      --remote-port <端口>        要开放在服务器上的公网端口
      --local-ip <ip>             本地监听 IP，默认 127.0.0.1
      --local-port <端口>         本地监听端口
      --secret <密钥>             Ubuntu 端打印的 API 密钥
      --protocol <tcp|udp>        默认 tcp
      --frpc <路径>               自定义 frpc.exe 路径，默认使用内置 frpc

    不传选项时会逐项询问。

    安全提示: 控制平面使用明文 HTTP，请确保仅在受信网络/内网中使用。
    """);
}
