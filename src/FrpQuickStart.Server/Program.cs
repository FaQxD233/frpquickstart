using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
Console.WriteLine($"frps 端口: {settings.FrpsBindPort}");

// ROB-6: 仅在首次生成密钥时明文显示，后续启动提示从配置文件查看
if (settings.IsNewlyCreated)
{
    Console.WriteLine($"API 密钥: {settings.ApiSecret} (首次生成，请妥善保存，之后不会再显示)");
}
else
{
    Console.WriteLine("API 密钥: (已在配置文件中，使用 cat server-config.json 查看)");
}

if (string.IsNullOrWhiteSpace(settings.PublicAddress))
{
    Console.WriteLine("PublicAddress 未设置，将让 Windows 客户端使用它输入的服务器 IP。");
}
else
{
    Console.WriteLine($"公网地址: {settings.PublicAddress}");
}

Console.WriteLine();

// TLS 模式选择
var tlsMode = GetOption(args, "--tls")?.Trim().ToLowerInvariant();
if (tlsMode is null && string.Equals(settings.TlsMode, "none", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("是否启用 TLS 加密?");
    Console.WriteLine("  none        - 不加密 (默认，仅限受信网络)");
    Console.WriteLine("  self-signed - 自动生成自签证书");
    Console.WriteLine("  acme        - 使用 acme.sh 获取的 Let's Encrypt IP 证书");
    Console.Write("请选择 [none/self-signed/acme]: ");
    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
    tlsMode = input switch
    {
        "self-signed" or "1" => "self-signed",
        "acme" or "2" => "acme",
        _ => "none"
    };
}
else if (tlsMode is null)
{
    tlsMode = settings.TlsMode;
}

if (tlsMode is not "none" and not "self-signed" and not "acme")
{
    Console.Error.WriteLine($"未知 TLS 模式: {tlsMode}，请使用 none/self-signed/acme。");
    return;
}

TlsCertificateProvider? tlsCertificateProvider = null;
string? tlsFingerprint = null;

if (tlsMode == "self-signed")
{
    var certPath = Path.Combine(runtimeDir, "server-cert.pfx");
    if (File.Exists(certPath))
    {
        // SEC-2 FIX: 从密码文件加载密码
        var passwordPath = certPath + ".password";
        if (!File.Exists(passwordPath))
        {
            Console.Error.WriteLine($"[错误] 证书密码文件不存在: {passwordPath}");
            Console.Error.WriteLine("证书文件已损坏或被篡改。请删除证书后重新生成。");
            return;
        }

        var password = File.ReadAllText(passwordPath, Encoding.UTF8).Trim();
        var serverCert = X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
        tlsCertificateProvider = TlsCertificateProvider.FromCertificate(serverCert);
        Console.WriteLine($"TLS: 加载已有自签证书 {certPath}");
    }
    else
    {
        var serverCert = GenerateSelfSignedCert(settings, certPath);
        tlsCertificateProvider = TlsCertificateProvider.FromCertificate(serverCert);
        Console.WriteLine($"TLS: 已生成自签证书并保存到 {certPath}");
    }
    tlsFingerprint = tlsCertificateProvider.Fingerprint;
    Console.WriteLine($"TLS 证书指纹 (SHA256): {tlsFingerprint}");
    Console.WriteLine("请将此指纹告知 Windows 客户端用户，客户端需要该指纹验证证书。");
}
else if (tlsMode == "acme")
{
    AcmeCertificatePaths acmePaths;
    try
    {
        acmePaths = await EnsureAcmeCertificateAsync(settings, runtimeDir, args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[错误] ACME 证书准备失败: {ex.Message}");
        return;
    }

    tlsCertificateProvider = TlsCertificateProvider.FromPemFiles(acmePaths.FullChainPath, acmePaths.KeyPath);
    var serverCert = tlsCertificateProvider.CurrentCertificate;
    Console.WriteLine($"TLS: 加载 acme fullchain 证书 {acmePaths.FullChainPath}");

    // SEC-9 FIX: 证书过期时拒绝启动，即将过期时警告
    if (serverCert.NotAfter < DateTimeOffset.UtcNow)
    {
        Console.Error.WriteLine($"[错误] TLS 证书已过期 ({serverCert.NotAfter:u})。服务器拒绝启动，请重新获取证书。");
        return;
    }
    else if (serverCert.NotAfter < DateTimeOffset.UtcNow.AddDays(7))
    {
        Console.WriteLine($"[警告] TLS 证书将在 7 天内过期 ({serverCert.NotAfter:u})，请及时续期。");
    }
    Console.WriteLine("acme 证书由公共 CA 签发，客户端无需额外配置即可信任。");
    Console.WriteLine("acme.sh 续期后会覆盖 runtime/acme 下的证书文件；服务端会在新连接握手前自动重新加载。");
}

var protocolPrefix = tlsMode == "none" ? "http" : "https";
Console.WriteLine($"控制服务: {protocolPrefix}://{settings.ControlBindAddress}:{settings.ControlPort}/");

if (tlsMode == "none")
{
    Console.WriteLine("安全提示: 控制平面使用明文 HTTP，请确保仅在受信网络/内网中使用，或通过 SSH 隧道等加密通道访问。");
}
else
{
    Console.WriteLine($"安全: 控制平面已启用 TLS ({tlsMode})。");
}

var shareAddress = await ResolveShareAddressAsync(settings, args);
if (string.IsNullOrWhiteSpace(shareAddress))
{
    Console.WriteLine("[警告] 无法确定公网地址，未生成客户端分享链接。请设置 PublicAddress 或使用 --acme-id <公网IP>。");
}
else
{
    var shareLink = BuildShareLink(shareAddress, settings.ControlPort, tlsMode, settings.ApiSecret, tlsFingerprint);
    Console.WriteLine("客户端分享链接（包含 API 密钥，请只发给可信用户）:");
    Console.WriteLine(shareLink);
}
Console.WriteLine();

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

// BUG-5: 端口分配改用内存记录 + 锁，消除 TOCTOU 竞态
var allocatedPorts = new HashSet<int>();
var allocatedPortsLock = new object();

// ROB-1: 并发连接限制
var concurrencySemaphore = new SemaphoreSlim(100, 100);

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

// ROB-3: frps 进程健康监控
var frpsMonitorCts = new CancellationTokenSource();
if (frpsProcess is not null)
{
    _ = MonitorFrpsAsync(frpsProcess, frpsMonitorCts.Token);
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    frpsMonitorCts.Cancel();
    StopListener(listener);
    StopChild(frpsProcess);
};

Console.WriteLine("等待 Windows 客户端请求，按 Ctrl+C 退出。");

while (!frpsMonitorCts.Token.IsCancellationRequested)
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

    _ = Task.Run(() => HandleClientAsync(client, settings, runtimeDir, frpsProcess is not null, allocatedPorts, allocatedPortsLock, concurrencySemaphore, frpsMonitorCts.Token, tlsCertificateProvider, tlsMode, tlsFingerprint));
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

    var bundledFrpsResource = OperatingSystem.IsWindows()
        ? "FrpQuickStart.Bundled.frps.exe"
        : "FrpQuickStart.Bundled.frps";
    var frpsPath = ProcessHelpers.ResolveExecutable(settings.FrpsPath, "frps", bundledFrpsResource);
    Console.WriteLine($"启动 frps: {frpsPath} -c {frpsConfigPath}");
    var process = ProcessHelpers.StartFrp(frpsPath, frpsConfigPath);

    // QUAL-5: 改为检测端口是否开始监听（轮询），而非固定 750ms 超时
    var startTime = DateTime.UtcNow;
    var maxWait = TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow - startTime < maxWait)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"frps 启动后立即退出，退出码: {process.ExitCode}。请查看日志: {frpsLogPath}");
        }

        if (IsTcpPortOpen(IPAddress.Loopback, settings.FrpsBindPort, TimeSpan.FromMilliseconds(100)))
        {
            Console.WriteLine("frps 已启动并开始监听。");
            return process;
        }

        Thread.Sleep(200);
    }

    // 超时后检查是否已退出
    if (process.HasExited)
    {
        throw new InvalidOperationException($"frps 启动后退出，退出码: {process.ExitCode}。请查看日志: {frpsLogPath}");
    }

    Console.WriteLine("frps 启动超时(5s)但进程仍在运行，假设启动成功。");
    return process;
}

static X509Certificate2 GenerateSelfSignedCert(ServerSettings settings, string savePath)
{
    using var rsa = RSA.Create(2048);

    var subject = new X500DistinguishedName($"CN=FrpQuickStart-{Environment.MachineName}");
    var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    // Basic Constraints: not a CA
    request.CertificateExtensions.Add(
        new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

    // Key Usage: Digital Signature + Key Encipherment
    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

    // Extended Key Usage: Server Authentication
    request.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
            critical: true));

    // Subject Alternative Name: include server's public IP if known
    var sanBuilder = new SubjectAlternativeNameBuilder();
    if (!string.IsNullOrWhiteSpace(settings.PublicAddress))
    {
        if (IPAddress.TryParse(settings.PublicAddress, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(settings.PublicAddress);
        }
    }
    sanBuilder.AddIpAddress(IPAddress.Loopback); // always include 127.0.0.1 for testing
    request.CertificateExtensions.Add(sanBuilder.Build());

    // Subject Key Identifier
    request.CertificateExtensions.Add(
        new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

    var cert = request.CreateSelfSigned(
        DateTimeOffset.Now.AddDays(-1),
        DateTimeOffset.Now.AddYears(5));

    // SEC-2 FIX: 生成随机密码保护 PFX 私钥
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var pfxBytes = cert.Export(X509ContentType.Pfx, password);

    // SEC-8 FIX: 原子写入 - 先写临时文件再重命名
    var tempPath = savePath + ".tmp";
    File.WriteAllBytes(tempPath, pfxBytes);
    File.Move(tempPath, savePath, overwrite: true);

    // 将密码存储到单独文件，设置严格权限
    var passwordPath = savePath + ".password";
    File.WriteAllText(passwordPath, password, Encoding.UTF8);

    // Set Unix file permissions (owner read/write only)
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(savePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(passwordPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Also save PEM for inspection
    var pemPath = Path.ChangeExtension(savePath, ".pem");
    File.WriteAllText(pemPath, cert.ExportCertificatePem());

    Console.WriteLine($"[安全提示] 证书密码已保存到: {passwordPath}");
    Console.WriteLine("请妥善保管该密码文件，删除后证书将无法加载。");

    // Return a new instance with the private key available
    return X509CertificateLoader.LoadPkcs12(pfxBytes, password);
}

static async Task<AcmeCertificatePaths> EnsureAcmeCertificateAsync(ServerSettings settings, string runtimeDir, string[] args)
{
    if (OperatingSystem.IsWindows())
    {
        throw new InvalidOperationException("acme 模式当前依赖 acme.sh，请在 Linux/Ubuntu 服务端运行。");
    }

    var acmeDir = Path.Combine(runtimeDir, "acme");
    Directory.CreateDirectory(acmeDir);

    var fullChainPath = GetOption(args, "--tls-cert") ?? settings.TlsCertPath;
    if (string.IsNullOrWhiteSpace(fullChainPath))
    {
        fullChainPath = Path.Combine(acmeDir, "fullchain.pem");
    }

    var keyPath = GetOption(args, "--tls-key") ?? settings.TlsKeyPath;
    if (string.IsNullOrWhiteSpace(keyPath))
    {
        keyPath = Path.Combine(acmeDir, "key.pem");
    }

    var leafCertPath = Path.Combine(acmeDir, "cert.pem");
    fullChainPath = ResolveRuntimeFilePath(fullChainPath, runtimeDir, "证书路径");
    keyPath = ResolveRuntimeFilePath(keyPath, runtimeDir, "私钥路径");
    leafCertPath = ResolveRuntimeFilePath(leafCertPath, runtimeDir, "证书路径");

    var identifier = GetOption(args, "--acme-id") ?? settings.AcmeIdentifier;
    if (string.IsNullOrWhiteSpace(identifier))
    {
        identifier = settings.PublicAddress;
    }

    identifier = identifier.Trim();
    if (string.IsNullOrWhiteSpace(identifier))
    {
        throw new InvalidOperationException("acme 模式需要 ACME 标识符。请设置 PublicAddress、AcmeIdentifier，或传入 --acme-id <公网IP>。");
    }

    if (!IPAddress.TryParse(identifier, out _) && !IsValidHost(identifier))
    {
        throw new InvalidOperationException($"ACME 标识符无效: {identifier}");
    }

    var acmeShPath = ResolveAcmeShPath(GetOption(args, "--acme-sh") ?? settings.AcmeShPath);
    var renewDays = GetIntOption(args, "--acme-renew-days") ?? settings.AcmeRenewDays;
    if (renewDays < 1)
    {
        throw new InvalidOperationException("AcmeRenewDays 必须大于 0。");
    }

    var server = (HasFlag(args, "--acme-staging") || settings.AcmeStaging) ? "letsencrypt_test" : "letsencrypt";
    var profile = GetOption(args, "--acme-profile") ?? settings.AcmeProfile;
    var keyLength = GetOption(args, "--acme-key-length") ?? settings.AcmeKeyLength;
    var email = GetOption(args, "--acme-email") ?? settings.AcmeAccountEmail;

    Console.WriteLine($"ACME: 使用 acme.sh 为 {identifier} 申请/续期 Let's Encrypt 证书。");
    Console.WriteLine($"ACME: 输出 fullchain={fullChainPath}, key={keyPath}, renewDays={renewDays}, server={server}");
    if (!string.IsNullOrWhiteSpace(profile))
    {
        Console.WriteLine($"ACME: 请求证书 profile={profile}");
    }
    if (!string.IsNullOrWhiteSpace(keyLength))
    {
        Console.WriteLine($"ACME: 请求证书 keyLength={keyLength}");
    }

    var issueArgs = new List<string>
    {
        "--issue",
        "--standalone",
        "--server", server,
        "-d", identifier,
        "--days", renewDays.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    if (!string.IsNullOrWhiteSpace(keyLength))
    {
        issueArgs.Add("--keylength");
        issueArgs.Add(keyLength.Trim());
    }

    if (!string.IsNullOrWhiteSpace(profile))
    {
        issueArgs.Add("--cert-profile");
        issueArgs.Add(profile.Trim());
    }

    if (!string.IsNullOrWhiteSpace(email))
    {
        issueArgs.Add("-m");
        issueArgs.Add(email.Trim());
    }

    await RunAcmeShAsync(acmeShPath, issueArgs, TimeSpan.FromMinutes(5), required: true);

    var installArgs = new List<string>
    {
        "--install-cert",
        "-d", identifier,
        "--cert-file", leafCertPath,
        "--key-file", keyPath,
        "--fullchain-file", fullChainPath
    };
    await RunAcmeShAsync(acmeShPath, installArgs, TimeSpan.FromMinutes(2), required: true);

    await RunAcmeShAsync(acmeShPath, new[] { "--install-cronjob" }, TimeSpan.FromMinutes(1), required: false);

    if (!File.Exists(fullChainPath))
    {
        throw new InvalidOperationException($"acme.sh 未生成 fullchain 文件: {fullChainPath}");
    }

    if (!File.Exists(keyPath))
    {
        throw new InvalidOperationException($"acme.sh 未生成私钥文件: {keyPath}");
    }

    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(fullChainPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (File.Exists(leafCertPath))
        {
            File.SetUnixFileMode(leafCertPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    return new AcmeCertificatePaths(fullChainPath, keyPath);
}

static string ResolveRuntimeFilePath(string path, string runtimeDir, string description)
{
    try
    {
        var fullPath = Path.GetFullPath(path);
        var allowedDir = Path.GetFullPath(runtimeDir);

        if (!IsPathInsideDirectory(fullPath, allowedDir))
        {
            throw new InvalidOperationException($"{description}必须在 {allowedDir} 目录内: {fullPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }
    catch (InvalidOperationException)
    {
        throw;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"{description}无效: {ex.Message}", ex);
    }
}

static bool IsPathInsideDirectory(string path, string directory)
{
    var relative = Path.GetRelativePath(directory, path);
    return relative != "." &&
        relative != ".." &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
        !Path.IsPathRooted(relative);
}

static string ResolveAcmeShPath(string configuredPath)
{
    var path = ExpandHomeDirectory(string.IsNullOrWhiteSpace(configuredPath) ? "~/.acme.sh/acme.sh" : configuredPath.Trim());
    if (Path.IsPathRooted(path) || path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar))
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到 acme.sh: {path}。请先安装 acme.sh，或通过 AcmeShPath/--acme-sh 指定路径。");
        }
    }

    return path;
}

static string ExpandHomeDirectory(string path)
{
    if (path == "~")
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    }

    return path;
}

static async Task RunAcmeShAsync(string acmeShPath, IReadOnlyCollection<string> arguments, TimeSpan timeout, bool required)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = acmeShPath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    Console.WriteLine($"ACME: 执行 {acmeShPath} {string.Join(' ', arguments.Select(EscapeForLog))}");

    using var process = new Process { StartInfo = startInfo };
    try
    {
        if (!process.Start())
        {
            throw new InvalidOperationException("进程未能启动。");
        }
    }
    catch (Win32Exception ex)
    {
        throw new InvalidOperationException($"无法启动 acme.sh: {ex.Message}", ex);
    }

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    var exitTask = process.WaitForExitAsync();
    var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
    if (completed != exitTask)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        throw new InvalidOperationException($"acme.sh 执行超时: {string.Join(' ', arguments.Select(EscapeForLog))}");
    }

    var stdout = (await stdoutTask).Trim();
    var stderr = (await stderrTask).Trim();
    if (!string.IsNullOrWhiteSpace(stdout))
    {
        Console.WriteLine(stdout);
    }

    if (process.ExitCode != 0)
    {
        var message = $"acme.sh 退出码 {process.ExitCode}: {stderr}";
        if (process.ExitCode == 2 && arguments.Contains("--issue"))
        {
            Console.WriteLine($"ACME: {message}");
            Console.WriteLine("ACME: 证书未到续期窗口，继续使用已安装证书。");
            return;
        }

        if (required)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine($"[警告] {message}");
        return;
    }

    if (!string.IsNullOrWhiteSpace(stderr))
    {
        Console.WriteLine(stderr);
    }
}

static string EscapeForLog(string value)
{
    return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}

// ROB-3: frps 进程监控，退出时打印警告
static async Task MonitorFrpsAsync(Process frpsProcess, CancellationToken ct)
{
    try
    {
        await frpsProcess.WaitForExitAsync(ct);
    }
    catch (OperationCanceledException)
    {
        return;
    }

    if (!ct.IsCancellationRequested)
    {
        Console.Error.WriteLine($"[警告] frps 进程意外退出，退出码: {frpsProcess.ExitCode}。新隧道请求将无法正常工作，请检查 frps 状态并考虑重启。");
    }
}

static async Task HandleClientAsync(TcpClient client, ServerSettings settings, string runtimeDir, bool frpsStartedByServer, HashSet<int> allocatedPorts, object allocatedPortsLock, SemaphoreSlim concurrencySemaphore, CancellationToken ct, TlsCertificateProvider? tlsCertificateProvider, string tlsMode, string? tlsFingerprint)
{
    // SEC-3 FIX: 在 TLS 握手前检查并发限制，避免浪费资源
    if (!await concurrencySemaphore.WaitAsync(5000, ct))
    {
        // 并发限制已满，直接关闭连接，不做 TLS 握手
        client.Dispose();
        return;
    }

    using (client)
    {
        // ROB-2: 读取超时，防止慢客户端永久占用连接
        client.ReceiveTimeout = 30_000;
        client.SendTimeout = 10_000;

        Stream stream = client.GetStream();
        try
        {
            if (tlsCertificateProvider is not null)
            {
                // SEC-6 FIX: 为 TLS 握手添加显式超时保护
                using var tlsTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, tlsTimeoutCts.Token);

                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(tlsCertificateProvider.CreateAuthenticationOptions(), linkedCts.Token);
                stream = sslStream;
            }

            var request = await ReadHttpRequestAsync(stream, ct);
            await HandleRequestAsync(stream, request, settings, runtimeDir, frpsStartedByServer, allocatedPorts, allocatedPortsLock, tlsMode, tlsFingerprint);
        }
        catch (AuthenticationException)
        {
            // SEC-10 FIX: 不暴露协议细节
            Console.Error.WriteLine("[连接失败] TLS 握手失败。");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[连接超时] TLS 握手或请求处理超时。");
        }
        catch (Exception ex)
        {
            // ROB-7: 500 错误返回通用消息，不暴露内部异常详情
            Console.Error.WriteLine($"[请求处理异常] {ex}");
            try
            {
                await WriteErrorAsync(stream, HttpStatusCode.InternalServerError, "服务器内部错误。");
            }
            catch
            {
                // 客户端可能已断开
            }
        }
        finally
        {
            concurrencySemaphore.Release();
        }
    }
}

static async Task HandleRequestAsync(Stream responseStream, SimpleHttpRequest httpRequest, ServerSettings settings, string runtimeDir, bool frpsStartedByServer, HashSet<int> allocatedPorts, object allocatedPortsLock, string tlsMode, string? tlsFingerprint)
{
    try
    {
        // GET /health - 健康检查
        if (httpRequest.Method == "GET" && httpRequest.Path == "/health")
        {
            await WriteJsonAsync(responseStream, HttpStatusCode.OK, new HealthResponse
            {
                Success = true,
                Message = "ok",
                PublicAddress = settings.PublicAddress,
                FrpsBindPort = settings.FrpsBindPort,
                ControlPort = settings.ControlPort,
                FrpsStartedByServer = frpsStartedByServer,
                TlsMode = tlsMode,
                TlsFingerprint = tlsFingerprint ?? ""
            }, FrpQuickJsonContext.Default.HealthResponse);
            return;
        }

        // GET /admin - 管理界面
        if (httpRequest.Method == "GET" && httpRequest.Path == "/admin")
        {
            if (!IsAdminAuthorized(httpRequest, settings))
            {
                await ServeAdminLoginPage(responseStream);
                return;
            }

            await ServeAdminPage(responseStream);
            return;
        }

        // GET /api/tunnels/list - 获取隧道列表
        if (httpRequest.Method == "GET" && httpRequest.Path == "/api/tunnels/list")
        {
            if (!IsAdminAuthorized(httpRequest, settings))
            {
                await WriteErrorAsync(responseStream, HttpStatusCode.Unauthorized, "未授权。");
                return;
            }

            await ServeTunnelsList(responseStream, runtimeDir);
            return;
        }

        // GET /api/stats - 统计信息
        if (httpRequest.Method == "GET" && httpRequest.Path == "/api/stats")
        {
            if (!IsAdminAuthorized(httpRequest, settings))
            {
                await WriteErrorAsync(responseStream, HttpStatusCode.Unauthorized, "未授权。");
                return;
            }

            await ServeStats(responseStream, runtimeDir, allocatedPorts, allocatedPortsLock);
            return;
        }

        // POST /api/tunnels - 创建隧道（原有接口）
        if (httpRequest.Method != "POST" || httpRequest.Path != "/api/tunnels")
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.NotFound, "接口不存在。可用接口: GET /health, GET /admin, GET /api/tunnels/list, GET /api/stats, POST /api/tunnels");
            return;
        }

        // BUG-4: 反序列化单独 try-catch，返回 400 而非 500
        TunnelRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(httpRequest.Body, FrpQuickJsonContext.Default.TunnelRequest);
            if (request is null)
            {
                await WriteErrorAsync(responseStream, HttpStatusCode.BadRequest, "请求 JSON 无效。");
                return;
            }
        }
        catch (JsonException)
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.BadRequest, "请求 JSON 格式无效。");
            return;
        }

        var validationError = ValidateRequest(request, settings);
        if (validationError is not null)
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.BadRequest, validationError);
            return;
        }

        // BUG-5: 端口分配使用内存记录 + 锁，消除 TOCTOU 竞态
        bool portAllocated;
        lock (allocatedPortsLock)
        {
            if (allocatedPorts.Contains(request.RemotePort))
            {
                portAllocated = false;
            }
            else
            {
                // 仍然检查端口是否被占用（外部进程可能占用了）
                if (!IsRemotePortAvailable(request.Protocol, request.RemotePort))
                {
                    portAllocated = false;
                }
                else
                {
                    allocatedPorts.Add(request.RemotePort);
                    portAllocated = true;
                }
            }
        }

        if (!portAllocated)
        {
            await WriteErrorAsync(responseStream, HttpStatusCode.Conflict, $"服务器端口 {request.RemotePort} 已被占用或已分配。");
            return;
        }

        var proxyName = FrpConfigWriter.SafeProxyName(request.ClientName, request.RemotePort, request.Protocol);
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
        Console.Error.WriteLine($"[请求处理未预期异常] {ex}");
        await WriteErrorAsync(responseStream, HttpStatusCode.InternalServerError, "服务器内部错误。");
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
    // BUG-6: 对 UDP 使用 Socket 绑定检测，更可靠
    if (string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
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
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static bool IsAdminAuthorized(SimpleHttpRequest request, ServerSettings settings)
{
    var suppliedSecret = GetQueryParameter(request.QueryString, "secret");
    if (string.IsNullOrWhiteSpace(suppliedSecret) &&
        request.Headers.TryGetValue("X-Api-Secret", out var headerSecret))
    {
        suppliedSecret = headerSecret;
    }

    if (string.IsNullOrWhiteSpace(suppliedSecret) &&
        request.Headers.TryGetValue("Authorization", out var authorization) &&
        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        suppliedSecret = authorization["Bearer ".Length..].Trim();
    }

    return !string.IsNullOrEmpty(suppliedSecret) && FixedTimeEquals(suppliedSecret, settings.ApiSecret);
}

static string? GetQueryParameter(string queryString, string name)
{
    if (string.IsNullOrWhiteSpace(queryString))
    {
        return null;
    }

    var query = queryString.StartsWith("?", StringComparison.Ordinal) ? queryString[1..] : queryString;
    foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = part.Split('=', 2);
        var key = Uri.UnescapeDataString(pair[0].Replace("+", " ", StringComparison.Ordinal));
        if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        return pair.Length == 2
            ? Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal))
            : "";
    }

    return null;
}

static void AppendTunnelRecord(string runtimeDir, TunnelRequest request, string proxyName)
{
    // ROB-9 FIX: 使用命名类型代替匿名类型以支持 trimming
    var record = new TunnelRecord
    {
        Time = DateTimeOffset.UtcNow,
        Protocol = request.Protocol,
        RemotePort = request.RemotePort,
        LocalIp = request.LocalIp,
        LocalPort = request.LocalPort,
        ClientName = request.ClientName,
        ProxyName = proxyName
    };
    var json = JsonSerializer.Serialize(record, FrpQuickJsonContext.Default.TunnelRecord);

    var recordPath = Path.Combine(runtimeDir, "tunnels.jsonl");

    // ROB-5: tunnels.jsonl 大小限制，超过 10MB 轮转
    const long maxRecordFileSize = 10 * 1024 * 1024;
    try
    {
        var fileInfo = new FileInfo(recordPath);
        if (fileInfo.Exists && fileInfo.Length > maxRecordFileSize)
        {
            var backupPath = recordPath + "." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(recordPath, backupPath, overwrite: true);
        }
    }
    catch
    {
        // 轮转失败不阻塞记录写入
    }

    File.AppendAllText(recordPath, json + Environment.NewLine, Encoding.UTF8);
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

static async Task<SimpleHttpRequest> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
{
    var received = new List<byte>(4096);
    var buffer = new byte[4096];
    var headerEnd = -1;

    // ROB-2: 读取超时保护
    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    readCts.CancelAfter(TimeSpan.FromSeconds(30));

    while (headerEnd < 0)
    {
        var read = await stream.ReadAsync(buffer, readCts.Token);
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
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i < lines.Length; i++)
    {
        var separator = lines[i].IndexOf(':');
        if (separator <= 0)
        {
            continue;
        }

        var name = lines[i][..separator].Trim();
        var value = lines[i][(separator + 1)..].Trim();
        headers[name] = value;
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
        var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), readCts.Token);
        if (read == 0)
        {
            throw new InvalidOperationException("HTTP 请求体未完整传输。");
        }

        offset += read;
    }

    var targetParts = requestParts[1].Split('?', 2);
    var path = targetParts[0];
    var queryString = targetParts.Length == 2 ? targetParts[1] : "";
    return new SimpleHttpRequest(requestParts[0].ToUpperInvariant(), path, queryString, headers, body);
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

static async Task<string> ResolveShareAddressAsync(ServerSettings settings, string[] args)
{
    var configured = GetOption(args, "--acme-id");
    if (string.IsNullOrWhiteSpace(configured))
    {
        configured = settings.AcmeIdentifier;
    }

    if (string.IsNullOrWhiteSpace(configured))
    {
        configured = settings.PublicAddress;
    }

    configured = NormalizeShareAddress(configured);
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    var discovered = await TryDiscoverPublicIpv4Async();
    return discovered ?? "";
}

static string NormalizeShareAddress(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "";
    }

    var trimmed = value.Trim();
    if (trimmed is "0.0.0.0" or "*" || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return "";
    }

    if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
    {
        return uri.Host;
    }

    return trimmed;
}

static async Task<string?> TryDiscoverPublicIpv4Async()
{
    var endpoints = new[]
    {
        "https://api.ipify.org",
        "https://ip.sb",
        "https://ipv4.icanhazip.com"
    };

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    foreach (var endpoint in endpoints)
    {
        try
        {
            var value = (await http.GetStringAsync(endpoint)).Trim();
            if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return value;
            }
        }
        catch
        {
            // Try the next public IP service.
        }
    }

    return null;
}

static string BuildShareLink(string serverAddress, int controlPort, string tlsMode, string apiSecret, string? tlsFingerprint)
{
    var builder = new UriBuilder("frpquick", serverAddress, controlPort);
    var query = new List<string>
    {
        "tls=" + Uri.EscapeDataString(tlsMode),
        "secret=" + Uri.EscapeDataString(apiSecret)
    };

    if (!string.IsNullOrWhiteSpace(tlsFingerprint))
    {
        query.Add("fp=" + Uri.EscapeDataString(tlsFingerprint));
    }

    builder.Query = string.Join("&", query);
    return builder.Uri.ToString();
}

static string ReasonPhrase(HttpStatusCode statusCode)
{
    return statusCode switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.Unauthorized => "Unauthorized",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.Conflict => "Conflict",
        HttpStatusCode.ServiceUnavailable => "Service Unavailable",
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

static int? GetIntOption(string[] args, string name)
{
    var value = GetOption(args, name);
    if (value is null)
    {
        return null;
    }

    if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed;
    }

    throw new InvalidOperationException($"{name} 必须是整数。");
}

static bool HasFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
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
      frpquick-server [--config server-config.json] [--tls <mode>]

    选项:
      --config <path>           配置文件路径，默认 server-config.json
      --tls <mode>              TLS 模式: none (默认) / self-signed / acme
      --tls-cert <path>         ACME fullchain 输出路径，默认 runtime/acme/fullchain.pem
      --tls-key <path>          ACME 私钥输出路径，默认 runtime/acme/key.pem
      --acme-id <ip-or-host>    ACME 标识符，IP 证书填写公网 IP；默认使用 PublicAddress
      --acme-email <email>      ACME 账户邮箱
      --acme-sh <path>          acme.sh 路径，默认 ~/.acme.sh/acme.sh
      --acme-renew-days <days>  acme.sh 续期间隔，默认 7
      --acme-profile <profile>  ACME cert profile，默认 shortlived；设为空可禁用
      --acme-key-length <value>  ACME 密钥长度/类型，默认 2048；可用 ec-256 等
      --acme-staging           使用 Let's Encrypt staging 环境测试

    首次启动会生成 server-config.json，并打印 API 密钥。
    默认使用内置 frps；如需覆盖，请修改 server-config.json 的 FrpsPath。

    TLS 模式:
      none        - 明文 HTTP，仅限受信网络/内网使用
      self-signed - 自动生成自签证书，打印指纹供客户端验证
      acme        - 调用 acme.sh 获取 Let's Encrypt shortlived IP 证书 (公网信任)

    安全提示: 使用 TLS 加密可防止密钥和 FrpAuthToken 被中间人窃取。
    """);
}

// 管理界面 API
static async Task ServeAdminPage(Stream responseStream)
{
    var html = GetAdminPageHtml();
    var bytes = Encoding.UTF8.GetBytes(html);

    var headers = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n\r\n";
    var headerBytes = Encoding.UTF8.GetBytes(headers);

    await responseStream.WriteAsync(headerBytes);
    await responseStream.WriteAsync(bytes);
    await responseStream.FlushAsync();
}

static async Task ServeAdminLoginPage(Stream responseStream)
{
    var html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>FRP QuickStart 管理面板</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f5f7fb; color: #202124; }
        main { width: min(420px, calc(100vw - 32px)); background: #fff; border: 1px solid #dfe3ea; border-radius: 8px; padding: 28px; box-shadow: 0 10px 30px rgba(20, 30, 50, 0.08); }
        h1 { font-size: 22px; margin: 0 0 18px; }
        label { display: block; font-size: 14px; font-weight: 600; margin-bottom: 8px; }
        input { width: 100%; box-sizing: border-box; padding: 11px 12px; border: 1px solid #c8ced8; border-radius: 6px; font-size: 15px; }
        button { margin-top: 16px; width: 100%; padding: 11px 14px; border: 0; border-radius: 6px; background: #2454d6; color: #fff; font-size: 15px; font-weight: 700; cursor: pointer; }
        .error { color: #b42318; margin-top: 12px; min-height: 20px; font-size: 14px; }
    </style>
</head>
<body>
    <main>
        <h1>FRP QuickStart 管理面板</h1>
        <form id="loginForm">
            <label for="secret">API 密钥</label>
            <input id="secret" name="secret" type="password" autocomplete="current-password" required autofocus>
            <button type="submit">进入</button>
            <div class="error" id="error"></div>
        </form>
    </main>
    <script>
        document.getElementById('loginForm').addEventListener('submit', async (event) => {
            event.preventDefault();
            const secret = document.getElementById('secret').value.trim();
            const error = document.getElementById('error');
            error.textContent = '';
            try {
                const res = await fetch('/api/stats', { headers: { 'X-Api-Secret': secret } });
                if (!res.ok) {
                    error.textContent = '密钥无效';
                    return;
                }
                localStorage.setItem('frpquick_api_secret', secret);
                location.href = '/admin?secret=' + encodeURIComponent(secret);
            } catch (err) {
                error.textContent = '无法连接管理 API';
            }
        });
    </script>
</body>
</html>
""";
    var bytes = Encoding.UTF8.GetBytes(html);
    var headers = $"HTTP/1.1 401 Unauthorized\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
    await responseStream.WriteAsync(Encoding.UTF8.GetBytes(headers));
    await responseStream.WriteAsync(bytes);
    await responseStream.FlushAsync();
}

static async Task ServeTunnelsList(Stream responseStream, string runtimeDir)
{
    var tunnelsFile = Path.Combine(runtimeDir, "tunnels.jsonl");
    var tunnels = new List<TunnelRecord>();

    if (File.Exists(tunnelsFile))
    {
        foreach (var line in File.ReadLines(tunnelsFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize(line, FrpQuickJsonContext.Default.TunnelRecord);
                if (record != null)
                {
                    // 检测端口是否在线
                    record.IsOnline = IsPortListening(record.RemotePort);
                    tunnels.Add(record);
                }
            }
            catch { /* 跳过损坏的行 */ }
        }
    }

    // 按时间倒序
    tunnels.Reverse();

    await WriteJsonAsync(responseStream, HttpStatusCode.OK, new TunnelListResponse
    {
        Success = true,
        Tunnels = tunnels
    }, FrpQuickJsonContext.Default.TunnelListResponse);
}

static bool IsPortListening(int port)
{
    try
    {
        // 检查端口是否被监听（frps 在监听表示隧道在线）
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, port);
        using var socket = new System.Net.Sockets.Socket(endpoint.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            socket.Bind(endpoint);
            // 可以绑定说明没被占用，隧道离线
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            // 绑定失败说明端口被占用，隧道在线
            return true;
        }
    }
    catch
    {
        return false;
    }
}

static async Task ServeStats(Stream responseStream, string runtimeDir, HashSet<int> allocatedPorts, object allocatedPortsLock)
{
    var tunnelsFile = Path.Combine(runtimeDir, "tunnels.jsonl");
    var totalTunnels = 0;
    var uniqueClients = new HashSet<string>();
    var allPorts = new HashSet<int>();

    if (File.Exists(tunnelsFile))
    {
        foreach (var line in File.ReadLines(tunnelsFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            totalTunnels++;
            try
            {
                var record = JsonSerializer.Deserialize(line, FrpQuickJsonContext.Default.TunnelRecord);
                if (record != null)
                {
                    if (!string.IsNullOrEmpty(record.ClientName))
                    {
                        uniqueClients.Add(record.ClientName);
                    }
                    allPorts.Add(record.RemotePort);
                }
            }
            catch { /* 跳过 */ }
        }
    }

    // 实时检测哪些端口真正在线
    var actuallyOccupied = allPorts.Where(IsPortListening).ToArray();

    await WriteJsonAsync(responseStream, HttpStatusCode.OK, new StatsResponse
    {
        Success = true,
        TotalTunnels = totalTunnels,
        UniqueClients = uniqueClients.Count,
        OccupiedPorts = actuallyOccupied
    }, FrpQuickJsonContext.Default.StatsResponse);
}

static string GetAdminPageHtml()
{
    return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>FRP QuickStart 管理面板</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            padding: 20px;
        }
        .container {
            max-width: 1200px;
            margin: 0 auto;
        }
        .header {
            background: rgba(255,255,255,0.95);
            padding: 30px;
            border-radius: 10px;
            margin-bottom: 20px;
            box-shadow: 0 4px 6px rgba(0,0,0,0.1);
        }
        .header h1 {
            font-size: 28px;
            color: #333;
            margin-bottom: 10px;
        }
        .stats {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 15px;
            margin-bottom: 20px;
        }
        .stat-card {
            background: rgba(255,255,255,0.95);
            padding: 20px;
            border-radius: 10px;
            box-shadow: 0 4px 6px rgba(0,0,0,0.1);
        }
        .stat-value {
            font-size: 32px;
            font-weight: bold;
            color: #667eea;
            margin-bottom: 5px;
        }
        .stat-label {
            color: #666;
            font-size: 14px;
        }
        .tunnels-section {
            background: rgba(255,255,255,0.95);
            padding: 30px;
            border-radius: 10px;
            box-shadow: 0 4px 6px rgba(0,0,0,0.1);
        }
        .section-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 20px;
        }
        .section-header h2 {
            font-size: 20px;
            color: #333;
        }
        .refresh-btn {
            background: #667eea;
            color: white;
            border: none;
            padding: 10px 20px;
            border-radius: 5px;
            cursor: pointer;
            font-size: 14px;
        }
        .refresh-btn:hover {
            background: #5568d3;
        }
        table {
            width: 100%;
            border-collapse: collapse;
        }
        th, td {
            padding: 12px;
            text-align: left;
            border-bottom: 1px solid #e0e0e0;
        }
        th {
            background: #f5f5f5;
            font-weight: 600;
            color: #333;
        }
        tr:hover {
            background: #f9f9f9;
        }
        .badge {
            display: inline-block;
            padding: 4px 8px;
            border-radius: 3px;
            font-size: 12px;
            font-weight: 500;
        }
        .badge-tcp {
            background: #e3f2fd;
            color: #1976d2;
        }
        .badge-udp {
            background: #fff3e0;
            color: #f57c00;
        }
        .status-online {
            color: #4caf50;
            font-weight: bold;
        }
        .status-offline {
            color: #9e9e9e;
        }
        .loading {
            text-align: center;
            padding: 40px;
            color: #666;
        }
        .empty {
            text-align: center;
            padding: 40px;
            color: #999;
        }
        .port-list {
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
            margin-top: 10px;
        }
        .port-tag {
            background: #e8eaf6;
            color: #5c6bc0;
            padding: 4px 12px;
            border-radius: 15px;
            font-size: 13px;
            font-weight: 500;
        }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <h1>🚀 FRP QuickStart 管理面板</h1>
            <p style="color: #666; margin-top: 5px;">实时监控和管理内网穿透隧道</p>
        </div>

        <div class="stats">
            <div class="stat-card">
                <div class="stat-value" id="totalTunnels">-</div>
                <div class="stat-label">历史隧道总数</div>
            </div>
            <div class="stat-card">
                <div class="stat-value" id="uniqueClients">-</div>
                <div class="stat-label">独立客户端数</div>
            </div>
            <div class="stat-card">
                <div class="stat-value" id="occupiedPorts">-</div>
                <div class="stat-label">已占用端口数</div>
            </div>
        </div>

        <div class="tunnels-section">
            <div class="section-header">
                <h2>📋 隧道记录</h2>
                <button class="refresh-btn" onclick="loadData()">🔄 刷新</button>
            </div>
            <div id="tunnelsTable">
                <div class="loading">加载中...</div>
            </div>
        </div>
    </div>

    <script>
        const urlSecret = new URLSearchParams(window.location.search).get('secret');
        if (urlSecret) {
            localStorage.setItem('frpquick_api_secret', urlSecret);
            history.replaceState(null, '', '/admin');
        }

        function authHeaders() {
            const secret = localStorage.getItem('frpquick_api_secret') || '';
            return { 'X-Api-Secret': secret };
        }

        async function loadData() {
            try {
                // 加载统计数据
                const statsRes = await fetch('/api/stats', { headers: authHeaders() });
                if (statsRes.status === 401) {
                    localStorage.removeItem('frpquick_api_secret');
                    location.href = '/admin';
                    return;
                }
                const stats = await statsRes.json();

                document.getElementById('totalTunnels').textContent = stats.TotalTunnels;
                document.getElementById('uniqueClients').textContent = stats.UniqueClients;
                document.getElementById('occupiedPorts').textContent = stats.OccupiedPorts.length;

                // 加载隧道列表
                const tunnelsRes = await fetch('/api/tunnels/list', { headers: authHeaders() });
                if (tunnelsRes.status === 401) {
                    localStorage.removeItem('frpquick_api_secret');
                    location.href = '/admin';
                    return;
                }
                const tunnelsData = await tunnelsRes.json();

                const container = document.getElementById('tunnelsTable');

                if (!tunnelsData.Tunnels || tunnelsData.Tunnels.length === 0) {
                    container.innerHTML = '<div class="empty">暂无隧道记录</div>';
                    return;
                }

                const table = document.createElement('table');
                table.innerHTML = `
                    <thead>
                        <tr>
                            <th>状态</th>
                            <th>客户端名称</th>
                            <th>协议</th>
                            <th>本地地址</th>
                            <th>公网端口</th>
                            <th>代理名称</th>
                            <th>创建时间</th>
                        </tr>
                    </thead>
                    <tbody>
                        ${tunnelsData.Tunnels.map(t => `
                            <tr>
                                <td class="${t.IsOnline ? 'status-online' : 'status-offline'}">
                                    ${t.IsOnline ? '🟢 在线' : '🔴 离线'}
                                </td>
                                <td><strong>${escapeHtml(t.ClientName)}</strong></td>
                                <td><span class="badge badge-${t.Protocol.toLowerCase()}">${t.Protocol.toUpperCase()}</span></td>
                                <td>${escapeHtml(t.LocalIp)}:${t.LocalPort}</td>
                                <td><strong>${t.RemotePort}</strong></td>
                                <td style="font-size: 12px; color: #666;">${escapeHtml(t.ProxyName)}</td>
                                <td>${formatTime(t.Time)}</td>
                            </tr>
                        `).join('')}
                    </tbody>
                `;

                container.innerHTML = '';
                container.appendChild(table);

            } catch (err) {
                document.getElementById('tunnelsTable').innerHTML =
                    `<div class="empty">加载失败: ${err.message}</div>`;
            }
        }

        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        function formatTime(isoString) {
            const date = new Date(isoString);
            return date.toLocaleString('zh-CN', {
                year: 'numeric',
                month: '2-digit',
                day: '2-digit',
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit'
            });
        }

        // 页面加载时自动刷新
        loadData();

        // 每 30 秒自动刷新
        setInterval(loadData, 30000);
    </script>
</body>
</html>
""";
}

internal sealed record AcmeCertificatePaths(string FullChainPath, string KeyPath);

internal sealed class TlsCertificateProvider
{
    private readonly object _lock = new();
    private readonly string? _fullChainPath;
    private readonly string? _keyPath;
    private X509Certificate2 _certificate;
    private SslStreamCertificateContext _certificateContext;
    private DateTime _fullChainLastWriteUtc;
    private DateTime _keyLastWriteUtc;

    private TlsCertificateProvider(
        X509Certificate2 certificate,
        SslStreamCertificateContext certificateContext,
        string? fullChainPath,
        string? keyPath)
    {
        _certificate = certificate;
        _certificateContext = certificateContext;
        _fullChainPath = fullChainPath;
        _keyPath = keyPath;
        RefreshWriteTimes();
    }

    public static TlsCertificateProvider FromCertificate(X509Certificate2 certificate)
    {
        return new TlsCertificateProvider(
            certificate,
            SslStreamCertificateContext.Create(certificate, additionalCertificates: null),
            fullChainPath: null,
            keyPath: null);
    }

    public static TlsCertificateProvider FromPemFiles(string fullChainPath, string keyPath)
    {
        var (certificate, context) = LoadPemContext(fullChainPath, keyPath);
        return new TlsCertificateProvider(certificate, context, fullChainPath, keyPath);
    }

    public X509Certificate2 CurrentCertificate
    {
        get
        {
            lock (_lock)
            {
                ReloadIfChanged();
                return _certificate;
            }
        }
    }

    public string Fingerprint
    {
        get
        {
            lock (_lock)
            {
                ReloadIfChanged();
                return _certificate.GetCertHashString(HashAlgorithmName.SHA256);
            }
        }
    }

    public SslServerAuthenticationOptions CreateAuthenticationOptions()
    {
        lock (_lock)
        {
            ReloadIfChanged();
            return new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _certificateContext,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };
        }
    }

    private void ReloadIfChanged()
    {
        if (_fullChainPath is null || _keyPath is null)
        {
            return;
        }

        var fullChainLastWriteUtc = File.GetLastWriteTimeUtc(_fullChainPath);
        var keyLastWriteUtc = File.GetLastWriteTimeUtc(_keyPath);
        if (fullChainLastWriteUtc == _fullChainLastWriteUtc && keyLastWriteUtc == _keyLastWriteUtc)
        {
            return;
        }

        var (certificate, context) = LoadPemContext(_fullChainPath, _keyPath);
        _certificate = certificate;
        _certificateContext = context;
        _fullChainLastWriteUtc = fullChainLastWriteUtc;
        _keyLastWriteUtc = keyLastWriteUtc;
        Console.WriteLine($"TLS: 检测到证书文件更新，已重新加载 {_fullChainPath}");
    }

    private void RefreshWriteTimes()
    {
        if (_fullChainPath is not null)
        {
            _fullChainLastWriteUtc = File.GetLastWriteTimeUtc(_fullChainPath);
        }

        if (_keyPath is not null)
        {
            _keyLastWriteUtc = File.GetLastWriteTimeUtc(_keyPath);
        }
    }

    private static (X509Certificate2 Certificate, SslStreamCertificateContext Context) LoadPemContext(string fullChainPath, string keyPath)
    {
        var certificate = X509Certificate2.CreateFromPemFile(fullChainPath, keyPath);
        var publicCertificates = LoadCertificatesFromPem(fullChainPath);
        var intermediates = new X509Certificate2Collection();
        for (var i = 1; i < publicCertificates.Count; i++)
        {
            intermediates.Add(publicCertificates[i]);
        }

        return (certificate, SslStreamCertificateContext.Create(certificate, intermediates));
    }

    private static X509Certificate2Collection LoadCertificatesFromPem(string path)
    {
        var text = File.ReadAllText(path, Encoding.ASCII);
        var certificates = new X509Certificate2Collection();
        const string beginMarker = "-----BEGIN CERTIFICATE-----";
        const string endMarker = "-----END CERTIFICATE-----";

        var searchIndex = 0;
        while (true)
        {
            var begin = text.IndexOf(beginMarker, searchIndex, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            begin += beginMarker.Length;
            var end = text.IndexOf(endMarker, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException($"证书 PEM 格式无效: {path}");
            }

            var base64 = text[begin..end]
                .Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal)
                .Trim();
            certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64)));
            searchIndex = end + endMarker.Length;
        }

        if (certificates.Count == 0)
        {
            throw new InvalidOperationException($"证书 PEM 中没有 CERTIFICATE 块: {path}");
        }

        return certificates;
    }
}

internal sealed record SimpleHttpRequest(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);
