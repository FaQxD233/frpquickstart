using System.Text.Json.Serialization;

namespace FrpQuickStart.Shared;

public sealed class TunnelRequest
{
    public string Secret { get; set; } = "";
    public int RemotePort { get; set; }
    public string LocalIp { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string ClientName { get; set; } = "";
}

public sealed class TunnelResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string FrpServerAddress { get; set; } = "";
    public int FrpServerPort { get; set; }
    public string Token { get; set; } = "";
    public int RemotePort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string ProxyName { get; set; } = "";
}

public sealed class HealthResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string PublicAddress { get; set; } = "";
    public int FrpsBindPort { get; set; }
    public int ControlPort { get; set; }
    public bool FrpsStartedByServer { get; set; }
    /// <summary>
    /// TLS 加密模式: "none" / "self-signed" / "acme"
    /// </summary>
    public string TlsMode { get; set; } = "none";
    /// <summary>
    /// 自签证书的 SHA256 指纹，供客户端验证。
    /// 仅在 TlsMode 为 "self-signed" 时有值。
    /// </summary>
    public string TlsFingerprint { get; set; } = "";
}

[JsonSerializable(typeof(TunnelRequest))]
[JsonSerializable(typeof(TunnelResponse))]
[JsonSerializable(typeof(HealthResponse))]
public partial class FrpQuickJsonContext : JsonSerializerContext;
