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
        sb.AppendLine("[[proxies]]");
        sb.AppendLine($"name = {TomlString(proxyName)}");
        sb.AppendLine($"type = {TomlString(protocol)}");
        sb.AppendLine($"localIP = {TomlString(localIp)}");
        sb.AppendLine($"localPort = {localPort.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"remotePort = {remotePort.ToString(CultureInfo.InvariantCulture)}");

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    public static void WriteFrpsToml(string path, int bindPort, string token, string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine($"bindPort = {bindPort.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("[auth]");
        sb.AppendLine($"token = {TomlString(token)}");
        sb.AppendLine();
        sb.AppendLine("[log]");
        sb.AppendLine($"to = {TomlString(logPath)}");
        sb.AppendLine("level = \"info\"");
        sb.AppendLine("maxDays = 3");

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
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
