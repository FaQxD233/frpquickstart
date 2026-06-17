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

        TlsMode = Environment.GetEnvironmentVariable("FRPQS_TLS_MODE") ?? TlsMode;
        TlsCertPath = Environment.GetEnvironmentVariable("FRPQS_TLS_CERT") ?? TlsCertPath;
        TlsKeyPath = Environment.GetEnvironmentVariable("FRPQS_TLS_KEY") ?? TlsKeyPath;
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

    private static string CreateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ServerSettings))]
public partial class ServerJsonContext : JsonSerializerContext;
