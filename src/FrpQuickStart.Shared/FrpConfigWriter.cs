using System.Globalization;
using System.Text;

namespace FrpQuickStart.Shared;

public static class FrpConfigWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteFrpcToml(
        string path,
        string serverAddress,
        int serverPort,
        string token,
        string frpTransportProtocol,
        string proxyName,
        string protocol,
        string localIp,
        int localPort,
        int remotePort)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine($"serverAddr = {TomlString(serverAddress)}");
        sb.AppendLine($"serverPort = {serverPort.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("[auth]");
        sb.AppendLine($"token = {TomlString(token)}");
        sb.AppendLine();

        var transport = NormalizeTransportProtocol(frpTransportProtocol);
        if (transport != "tcp")
        {
            sb.AppendLine("[transport]");
            if (transport == "wss")
            {
                sb.AppendLine("protocol = \"websocket\"");
                sb.AppendLine();
                sb.AppendLine("[transport.tls]");
                sb.AppendLine("enable = true");
            }
            else
            {
                sb.AppendLine($"protocol = {TomlString(transport)}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("[[proxies]]");
        sb.AppendLine($"name = {TomlString(proxyName)}");
        sb.AppendLine($"type = {TomlString(protocol)}");
        sb.AppendLine($"localIP = {TomlString(localIp)}");
        sb.AppendLine($"localPort = {localPort.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"remotePort = {remotePort.ToString(CultureInfo.InvariantCulture)}");

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    public static void WriteFrpsToml(
        string path,
        int bindPort,
        string token,
        string logPath,
        string frpTransportProtocol,
        string? tlsCertPath,
        string? tlsKeyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var transport = NormalizeTransportProtocol(frpTransportProtocol);
        var sb = new StringBuilder();
        sb.AppendLine($"bindPort = {bindPort.ToString(CultureInfo.InvariantCulture)}");
        if (transport == "kcp")
        {
            sb.AppendLine($"kcpBindPort = {bindPort.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (transport == "quic")
        {
            sb.AppendLine($"quicBindPort = {bindPort.ToString(CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine();
        sb.AppendLine("[auth]");
        sb.AppendLine($"token = {TomlString(token)}");
        sb.AppendLine();
        if (transport == "wss")
        {
            if (string.IsNullOrWhiteSpace(tlsCertPath) || string.IsNullOrWhiteSpace(tlsKeyPath))
            {
                throw new InvalidOperationException("wss 传输需要可供 frps 使用的 TLS 证书和私钥。");
            }

            sb.AppendLine("[transport.tls]");
            sb.AppendLine("force = true");
            sb.AppendLine($"certFile = {TomlString(tlsCertPath)}");
            sb.AppendLine($"keyFile = {TomlString(tlsKeyPath)}");
            sb.AppendLine();
        }

        sb.AppendLine("[log]");
        sb.AppendLine($"to = {TomlString(logPath)}");
        sb.AppendLine("level = \"info\"");
        sb.AppendLine("maxDays = 3");

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    public static string NormalizeTransportProtocol(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "tcp"
            : value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "ws" => "websocket",
            "tcp" or "kcp" or "quic" or "websocket" or "wss" => normalized,
            _ => throw new ArgumentException($"不支持的 frp 传输协议: {value}", nameof(value))
        };
    }

    public static bool IsSupportedTransportProtocol(string? value)
    {
        try
        {
            _ = NormalizeTransportProtocol(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成安全的 frp 代理名称。
    /// QUAL-2 FIX: 加入协议区分，防止同端口不同协议（TCP/UDP）名称冲突。
    /// </summary>
    public static string SafeProxyName(string clientName, int remotePort, string protocol = "tcp")
    {
        var source = string.IsNullOrWhiteSpace(clientName)
            ? Environment.MachineName
            : clientName.Trim();

        var safe = new StringBuilder();
        foreach (var ch in source)
        {
            safe.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        var normalized = safe.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "client";
        }

        // 加入协议区分：tcp-8080 vs udp-8080
        var protocolSuffix = string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        return $"{normalized}-{protocolSuffix}-{remotePort}";
    }

    /// <summary>
    /// ROB-4 FIX: 完整的 TOML 字符串转义，包括控制字符。
    /// TOML 规范要求转义: \b \t \n \f \r \" \\ 以及 \uXXXX
    /// </summary>
    private static string TomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(ch))
                    {
                        // 其他控制字符用 \uXXXX 转义
                        sb.Append($"\\u{((int)ch):X4}");
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
