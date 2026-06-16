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
            return loaded;
        }

        var settings = new ServerSettings
        {
            ApiSecret = CreateSecret(),
            FrpAuthToken = CreateSecret()
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

        if (string.IsNullOrWhiteSpace(FrpAuthToken))
        {
            FrpAuthToken = ApiSecret;
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
