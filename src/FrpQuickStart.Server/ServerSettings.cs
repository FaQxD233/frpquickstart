using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrpQuickStart.Server;

public sealed class ServerSettings
{
    public string ControlBindAddress { get; set; } = "0.0.0.0";
    public int ControlPort { get; set; } = 9080;
    public string PublicAddress { get; set; } = "";
    public int FrpsBindPort { get; set; } = 7000;
    public string FrpTransportProtocol { get; set; } = "tcp";
    public string ApiSecret { get; set; } = "";
    public string FrpAuthToken { get; set; } = "";
    public string FrpsPath { get; set; } = "frps";
    public int AllowedRemotePortStart { get; set; } = 10000;
    public int AllowedRemotePortEnd { get; set; } = 60000;
    public string RuntimeDirectory { get; set; } = "runtime";

    /// <summary>
    /// TLS 加密模式: "none" (明文 HTTP), "self-signed" (自动生成自签证书), "acme" (acme.sh IP 证书)
    /// </summary>
    public string TlsMode { get; set; } = "none";

    /// <summary>
    /// TLS 证书文件路径。
    /// self-signed 模式: PFX 文件路径（默认自动保存到 runtime/ 目录）。
    /// acme 模式: PEM 证书文件路径。
    /// </summary>
    public string TlsCertPath { get; set; } = "";

    /// <summary>
    /// TLS 私钥文件路径（仅 acme 模式使用，PEM 格式）。
    /// </summary>
    public string TlsKeyPath { get; set; } = "";

    /// <summary>
    /// acme.sh 可执行文件路径。默认使用 acme.sh 的常见安装位置。
    /// </summary>
    public string AcmeShPath { get; set; } = "~/.acme.sh/acme.sh";

    /// <summary>
    /// ACME 证书标识符。IP 证书请填写公网 IP；为空时使用 PublicAddress。
    /// </summary>
    public string AcmeIdentifier { get; set; } = "";

    /// <summary>
    /// ACME 账户邮箱。可为空，但生产环境建议填写。
    /// </summary>
    public string AcmeAccountEmail { get; set; } = "";

    /// <summary>
    /// acme.sh 续期间隔天数。Let's Encrypt shortlived IP 证书默认按 7 天续期。
    /// </summary>
    public int AcmeRenewDays { get; set; } = 7;

    /// <summary>
    /// ACME 证书 profile。Let's Encrypt shortlived 证书使用 "shortlived"。
    /// 如 acme.sh 版本不支持 --profile，可设置为空后退回默认证书。
    /// </summary>
    public string AcmeProfile { get; set; } = "shortlived";

    /// <summary>
    /// ACME 证书密钥长度/类型。Windows 客户端兼容性优先，默认使用 RSA 2048。
    /// acme.sh 也支持 ec-256、ec-384 等值。
    /// </summary>
    public string AcmeKeyLength { get; set; } = "2048";

    /// <summary>
    /// 是否使用 Let's Encrypt staging 环境测试申请流程。
    /// </summary>
    public bool AcmeStaging { get; set; }

    /// <summary>
    /// 标记是否为首次生成（首次启动时为 true，从已有配置文件加载时为 false）。
    /// 用于控制密钥是否明文打印到控制台。
    /// </summary>
    [JsonIgnore]
    public bool IsNewlyCreated { get; private set; }

    public static ServerSettings LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), ServerJsonContext.Default.ServerSettings)
                ?? throw new InvalidOperationException($"配置文件无效: {path}");
            var shouldSave = string.IsNullOrWhiteSpace(loaded.ApiSecret) || string.IsNullOrWhiteSpace(loaded.FrpAuthToken);
            loaded.EnsureSecrets();
            if (shouldSave)
            {
                Save(path, loaded);
            }

            loaded.ApplyEnvironmentOverrides();
            loaded.IsNewlyCreated = false;
            return loaded;
        }

        var settings = new ServerSettings
        {
            ApiSecret = CreateSecret(),
            FrpAuthToken = CreateSecret(),
            IsNewlyCreated = true
        };
        Save(path, settings);
        return settings;
    }

    private static void Save(string path, ServerSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, ServerJsonContext.Default.ServerSettings));
    }

    private void ApplyEnvironmentOverrides()
    {
        ApiSecret = Environment.GetEnvironmentVariable("FRPQS_SECRET") ?? ApiSecret;
        FrpAuthToken = Environment.GetEnvironmentVariable("FRPQS_FRP_TOKEN") ?? FrpAuthToken;

        if (int.TryParse(Environment.GetEnvironmentVariable("FRPQS_CONTROL_PORT"), out var controlPort))
        {
            ControlPort = controlPort;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("FRPQS_FRPS_PORT"), out var frpsPort))
        {
            FrpsBindPort = frpsPort;
        }

        PublicAddress = Environment.GetEnvironmentVariable("FRPQS_PUBLIC_ADDR") ?? PublicAddress;
        FrpTransportProtocol = Environment.GetEnvironmentVariable("FRPQS_FRP_TRANSPORT") ?? FrpTransportProtocol;

        TlsMode = Environment.GetEnvironmentVariable("FRPQS_TLS_MODE") ?? TlsMode;
        TlsCertPath = Environment.GetEnvironmentVariable("FRPQS_TLS_CERT") ?? TlsCertPath;
        TlsKeyPath = Environment.GetEnvironmentVariable("FRPQS_TLS_KEY") ?? TlsKeyPath;
        AcmeShPath = Environment.GetEnvironmentVariable("FRPQS_ACME_SH") ?? AcmeShPath;
        AcmeIdentifier = Environment.GetEnvironmentVariable("FRPQS_ACME_ID") ?? AcmeIdentifier;
        AcmeAccountEmail = Environment.GetEnvironmentVariable("FRPQS_ACME_EMAIL") ?? AcmeAccountEmail;
        AcmeProfile = Environment.GetEnvironmentVariable("FRPQS_ACME_PROFILE") ?? AcmeProfile;
        AcmeKeyLength = Environment.GetEnvironmentVariable("FRPQS_ACME_KEY_LENGTH") ?? AcmeKeyLength;

        if (int.TryParse(Environment.GetEnvironmentVariable("FRPQS_ACME_RENEW_DAYS"), out var acmeRenewDays))
        {
            AcmeRenewDays = acmeRenewDays;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("FRPQS_ACME_STAGING"), out var acmeStaging))
        {
            AcmeStaging = acmeStaging;
        }
    }

    private void EnsureSecrets()
    {
        if (string.IsNullOrWhiteSpace(ApiSecret))
        {
            ApiSecret = CreateSecret();
        }

        if (string.IsNullOrWhiteSpace(FrpAuthToken))
        {
            FrpAuthToken = CreateSecret();
        }
    }

    public void SaveTo(string path)
    {
        Save(path, this);
    }

    private static string CreateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ServerSettings))]
public partial class ServerJsonContext : JsonSerializerContext;
