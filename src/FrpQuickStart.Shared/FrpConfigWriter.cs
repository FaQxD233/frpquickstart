using System.Globalization;
using System.Text;

namespace FrpQuickStart.Shared;

public static class FrpConfigWriter
{
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

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
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

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static string SafeProxyName(string clientName, int remotePort)
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

        return $"{normalized}-{remotePort}";
    }

    private static string TomlString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
