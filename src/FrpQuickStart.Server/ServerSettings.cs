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
                // 补充缺失的密钥时也标记为非首次——已有配置说明不是全新部署
                Save(path, loaded);
            }

            loaded.ApplyEnvironmentOverrides();
            loaded.IsNewlyCreated = false;
            return loaded;
        }

        var settings = new ServerSettings
        {
            ApiSecret = CreateSecret(),
            FrpAuthToken = CreateSecret(),  // BUG-3: 独立生成，不再回退为 ApiSecret
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
    }

    private void EnsureSecrets()
    {
        if (string.IsNullOrWhiteSpace(ApiSecret))
        {
            ApiSecret = CreateSecret();
        }

        // BUG-3 FIX: FrpAuthToken 为空时独立生成，不再回退为 ApiSecret
        // 之前: FrpAuthToken = ApiSecret; 导致两个密钥相同，降低安全性
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
