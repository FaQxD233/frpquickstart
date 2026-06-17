# TLS 加密支持实现计划

## Context

当前 FrpQuickStart 的控制平面（客户端 → 服务端的 HTTP 通信）使用明文传输，密钥和 FrpAuthToken 可被中间人窃取。需要为控制平面加入 TLS 加密，支持两种模式让用户在运行时选择：

1. **自签证书 (self-signed)** — 服务器自动生成，客户端通过 SHA256 指纹验证
2. **acme IP 证书 (acme)** — 使用 acme.sh 外部获取的 Let's Encrypt IP 证书，客户端默认信任

HTTP 模式保持为默认，向后兼容。

## 修改文件清单

| 文件 | 改动概要 |
|------|----------|
| `src/FrpQuickStart.Shared/Models.cs` | HealthResponse 增加 TlsMode + TlsFingerprint |
| `src/FrpQuickStart.Server/ServerSettings.cs` | 增加 TlsMode/TlsCertPath/TlsKeyPath 属性 + 环境变量覆盖 |
| `src/FrpQuickStart.Server/Program.cs` | 核心改动：TLS 提示、证书生成/加载、SslStream 封装、ReadHttpRequestAsync 签名改 Stream、health 端点返回 TLS 信息、启动消息 |
| `src/FrpQuickStart.Client/Program.cs` | TLS 模式提示、--tls/--tls-fingerprint 参数、BuildControlUrl 支持 https、HttpClient 指纹验证回调 |

csproj / 发布脚本无需改动（System.Net.Security 和 X509Certificates 在 .NET 10 SDK 中内置）。

## 步骤 1: Models.cs — HealthResponse 增加 TLS 字段

```csharp
// HealthResponse 新增属性:
public string TlsMode { get; set; } = "none";       // "none" / "self-signed" / "acme"
public string TlsFingerprint { get; set; } = "";    // 自签证书的 SHA256 指纹
```

HealthResponse 已注册在 FrpQuickJsonContext 中，新属性自动参与序列化。

## 步骤 2: ServerSettings.cs — TLS 配置持久化

新增属性:
- `TlsMode` (string, 默认 "none")
- `TlsCertPath` (string, 默认 "")
- `TlsKeyPath` (string, 默认 "")

AddEnvironmentOverrides 新增:
- `FRPQS_TLS_MODE` → TlsMode
- `FRPQS_TLS_CERT` → TlsCertPath
- `FRPQS_TLS_KEY` → TlsKeyPath

## 步骤 3: Server/Program.cs — 服务端 TLS 实现

### 3a. TLS 模式交互式选择

启动时，如果 TlsMode 为 "none" 且未传 --tls 参数，提示用户选择:
- none — 不加密（默认，仅限受信网络）
- self-signed — 自动生成自签证书
- acme — 使用 acme.sh 获取的 Let's Encrypt IP 证书

支持 CLI: `--tls <none|self-signed|acme>`

### 3b. 证书加载/生成

**self-signed 模式**:
- 检查 `runtime/server-cert.pfx` 是否存在
- 存在则加载；不存在则用 `CertificateRequest.CreateSelfSigned()` 生成:
  - RSA 2048, CN=FrpQuickStart-<MachineName>
  - SAN: 服务器公网 IP (PublicAddress) + 127.0.0.1
  - EKU: Server Authentication
  - 有效期 5 年
  - 保存 PFX + PEM
  - 设置 Unix 文件权限 600 (仅 owner 可读)
- 打印 SHA256 指纹: `cert.GetCertHashString(HashAlgorithmName.SHA256)`
- 提示用户将指纹告知客户端操作员

**acme 模式**:
- 从 `--tls-cert` / `--tls-key` 参数或 TlsCertPath / TlsKeyPath 配置获取路径
- 使用 `X509Certificate2.CreateFromPemFile(certPath, keyPath)` 加载
- 检查证书是否即将过期/已过期，打印警告

### 3c. SslStream 封装

在 `HandleClientAsync` 中，`AcceptTcpClientAsync` 之后:
```
Stream stream = client.GetStream();
if (serverCert is not null)
{
    var ssl = new SslStream((NetworkStream)stream, leaveInnerStreamOpen: false);
    await ssl.AuthenticateAsServerAsync(serverCert, clientCertificateRequired: false,
        enabledSslProtocols: SslProtocols.Tls13 | SslProtocols.Tls12,
        checkCertificateRevocation: false);
    stream = ssl;
}
```

TLS 握手失败时捕获 `AuthenticationException`，提示协议不匹配。

并发拒绝路径（SemaphoreSlim 等待超时）同样需要 TLS 封装。

### 3d. ReadHttpRequestAsync 签名

`NetworkStream` → `Stream`（SslStream 继承 Stream，向下兼容）

### 3e. Health 端点返回 TLS 信息

HealthResponse 填充 tlsMode 和 tlsFingerprint。

### 3f. serverCert / tlsMode / tlsFingerprint 传递

通过闭包捕获（顶层语句中的变量已在作用域内）。

## 步骤 4: Client/Program.cs — 客户端 TLS 实现

### 4a. TLS 模式选择

新增参数:
- `--tls <none|self-signed|acme>` — TLS 模式
- `--tls-fingerprint <SHA256>` — 自签证书指纹

交互式提示: "TLS 加密模式 [none/self-signed/acme]"

### 4b. self-signed 模式: 指纹输入

如果选 self-signed，提示: "服务器 TLS 证书指纹 (SHA256，服务端启动时显示)"
指纹比较前去除冒号，统一大写。

### 4c. BuildControlUrl 支持 https

新增 `tlsMode` 参数: tlsMode != "none" 时生成 `https://` 前缀。

### 4d. HttpClient 指纹验证

```csharp
HttpMessageHandler handler;
if (tlsMode == "self-signed" && tlsFingerprint is not null)
{
    var expected = tlsFingerprint.ToUpperInvariant().Replace(":", "");
    handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null) return false;
            var actual = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
    };
}
else
{
    handler = new HttpClientHandler();
}
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
```

- none → 默认 handler + HTTP
- self-signed → 指纹固定验证
- acme → 默认 handler（Let's Encrypt 证书被 Windows 信任存储信任）

### 4e. 更新帮助文本

新增 --tls 和 --tls-fingerprint 说明。

## 验证方式

1. 编译: `dotnet build FrpQuickStart.sln`
2. 服务端自签模式测试: `frpquick-server --tls self-signed` → 确认生成 PFX、打印指纹
3. 客户端自签模式测试: `frpquick-client --tls self-signed --tls-fingerprint <指纹>` → 确认 TLS 握手成功
4. 服务端 acme 模式测试: 准备 PEM 证书文件，`frpquick-server --tls acme --tls-cert cert.pem --tls-key key.pem` → 确认加载成功
5. 客户端 acme 模式测试: `frpquick-client --tls acme` → 使用默认信任链
6. HTTP 向后兼容测试: 不传 --tls → 行为与之前完全一致
7. 错误场景: 指纹不匹配、证书过期、协议不匹配(HTTP 连 TLS 端口)
