using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FrpQuickStart.Shared;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return;
}

Console.OutputEncoding = Encoding.UTF8;

// 设置窗口标题
Console.Title = "FRP QuickStart Client";

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

var tlsMode = GetOption(args, "--tls")?.Trim().ToLowerInvariant()
    ?? PromptWithDefault("TLS 加密模式 [none/self-signed/acme]", "none");
if (tlsMode is not "none" and not "self-signed" and not "acme")
{
    Console.Error.WriteLine($"未知 TLS 模式: {tlsMode}");
    return;
}

string? tlsFingerprint = null;
if (tlsMode == "self-signed")
{
    tlsFingerprint = GetOption(args, "--tls-fingerprint")
        ?? PromptRequired("服务器 TLS 证书指纹 (SHA256，服务端启动时显示)");
}

var controlUrl = BuildControlUrl(serverHost, controlPort, tlsMode);

// SEC-5 FIX: 查询服务器 TLS 配置，防止降级攻击
Console.WriteLine("正在验证服务器 TLS 配置...");
try
{
    // 为 /health 查询创建支持自签证书的 HttpClient
    HttpMessageHandler healthHandler;
    if (tlsMode == "self-signed" && tlsFingerprint is not null)
    {
        var expectedFingerprint = tlsFingerprint.ToUpperInvariant().Replace(":", "").Replace(" ", "");
        healthHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, sslPolicyErrors) =>
            {
                if (cert is null) return false;

                // 自签名证书允许主机名不匹配和证书链不受信任
                var allowedErrors = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
                if ((sslPolicyErrors & ~allowedErrors) != SslPolicyErrors.None) return false;

                var now = DateTimeOffset.UtcNow;
                if (cert.NotBefore > now || cert.NotAfter < now) return false;

                try
                {
                    var actualFingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();
                    return string.Equals(actualFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        };
    }
    else
    {
        healthHandler = new HttpClientHandler();
    }

    using var healthHttp = new HttpClient(healthHandler) { Timeout = TimeSpan.FromSeconds(5) };
    var healthUrl = controlUrl.Replace("/api/tunnels", "").TrimEnd('/') + "/health";
    HealthResponse? health = null;

    try
    {
        health = await healthHttp.GetFromJsonAsync(healthUrl, FrpQuickJsonContext.Default.HealthResponse);
    }
    catch (HttpRequestException ex) when (tlsMode != "none")
    {
        Console.Error.WriteLine($"[警告] 无法通过 HTTPS 访问服务器健康检查: {ex.Message}");
        Console.Error.WriteLine("可能原因: 服务器未启用 TLS，或证书不受信任。");
        Console.Write("是否尝试使用 HTTP 明文连接? (yes/no): ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is "yes" or "y")
        {
            tlsMode = "none";
            controlUrl = BuildControlUrl(serverHost, controlPort, "none");
            health = await healthHttp.GetFromJsonAsync(controlUrl.Replace("/api/tunnels", "").TrimEnd('/') + "/health", FrpQuickJsonContext.Default.HealthResponse);
        }
        else
        {
            Console.Error.WriteLine("用户取消连接。");
            return;
        }
    }

    if (health is not null)
    {
        Console.WriteLine($"服务器 TLS 模式: {health.TlsMode}");

        // 检查客户端与服务器 TLS 模式是否匹配
        if (!string.Equals(health.TlsMode, tlsMode, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"[安全错误] TLS 模式不匹配！");
            Console.Error.WriteLine($"  服务器要求: {health.TlsMode}");
            Console.Error.WriteLine($"  客户端配置: {tlsMode}");
            Console.Error.WriteLine("可能的中间人攻击或配置错误。连接已拒绝。");
            return;
        }

        // 如果服务器返回指纹，验证是否匹配
        if (health.TlsMode == "self-signed" && !string.IsNullOrWhiteSpace(health.TlsFingerprint))
        {
            if (tlsFingerprint is not null)
            {
                var serverFp = health.TlsFingerprint.ToUpperInvariant().Replace(":", "").Replace(" ", "");
                var clientFp = tlsFingerprint.ToUpperInvariant().Replace(":", "").Replace(" ", "");
                if (!string.Equals(serverFp, clientFp, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"[安全错误] 证书指纹不匹配！");
                    Console.Error.WriteLine($"  服务器指纹: {health.TlsFingerprint}");
                    Console.Error.WriteLine($"  您输入的指纹: {tlsFingerprint}");
                    Console.Error.WriteLine("可能的中间人攻击或您输入了错误的指纹。连接已拒绝。");
                    return;
                }
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[警告] 无法验证服务器 TLS 配置: {ex.Message}");
    Console.WriteLine("将继续尝试连接，但请确保网络安全。");
}

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
    HttpMessageHandler handler;
    if (tlsMode == "self-signed" && tlsFingerprint is not null)
    {
        var expectedFingerprint = tlsFingerprint.ToUpperInvariant().Replace(":", "").Replace(" ", "");
        handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (httpRequestMessage, cert, chain, sslPolicyErrors) =>
            {
                if (cert is null) return false;

                // SEC-1 FIX: 检查证书有效性，即使指纹匹配也要拒绝过期/吊销证书
                // 自签名证书允许 RemoteCertificateNameMismatch (主机名不匹配) 和 RemoteCertificateChainErrors (链不受信任)
                var allowedErrors = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
                if ((sslPolicyErrors & ~allowedErrors) != SslPolicyErrors.None)
                {
                    Console.Error.WriteLine($"[TLS 验证失败] 证书存在安全问题: {sslPolicyErrors}");
                    return false;
                }

                // 显式检查证书过期
                var now = DateTimeOffset.UtcNow;
                if (cert.NotBefore > now || cert.NotAfter < now)
                {
                    Console.Error.WriteLine($"[TLS 验证失败] 证书已过期或尚未生效 (有效期: {cert.NotBefore:u} - {cert.NotAfter:u})");
                    return false;
                }

                // 指纹验证
                try
                {
                    var actualFingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();
                    return string.Equals(actualFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TLS 验证失败] 无法计算证书指纹: {ex.Message}");
                    return false;
                }
            }
        };
    }
    else
    {
        handler = new HttpClientHandler();
    }
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
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

// 更新窗口标题显示端口信息
Console.Title = $"FRP Client - 端口 {response.RemotePort} [{protocol.ToUpper()}]";

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

static string BuildControlUrl(string hostOrUrl, int port, string tlsMode)
{
    if (Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        return uri.ToString().TrimEnd('/');
    }

    var scheme = tlsMode == "none" ? "http" : "https";
    return $"{scheme}://{hostOrUrl}:{port}";
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

    TLS 选项:
      --tls <mode>                TLS 模式: none (默认) / self-signed / acme
      --tls-fingerprint <SHA256>  自签证书指纹 (self-signed 模式)

    不传选项时会逐项询问。

    TLS 模式说明:
      none        - 明文 HTTP，仅限受信网络/内网使用
      self-signed - 使用自签证书，需要服务端启动时显示的 SHA256 指纹
      acme        - 使用 acme.sh 获取的公网证书，客户端自动信任

    安全提示: 使用 --tls self-signed 或 --tls acme 可加密控制平面通信。
    """);
}
