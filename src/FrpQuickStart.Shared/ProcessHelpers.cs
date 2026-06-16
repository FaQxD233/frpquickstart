using System.ComponentModel;
using System.Diagnostics;

namespace FrpQuickStart.Shared;

public static class ProcessHelpers
{
    public static string ResolveExecutable(string configuredPath, string executableName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && configuredPath != executableName)
        {
            return configuredPath;
        }

        var fileName = OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName + ".exe"
            : executableName;

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, fileName);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        return configuredPath;
    }

    public static Process StartFrp(string executablePath, string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {executablePath}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"找不到或无法执行 {executablePath}。请把 frp 官方的对应二进制放到程序同目录，或加入 PATH。原始错误: {ex.Message}",
                ex);
        }
    }
}
