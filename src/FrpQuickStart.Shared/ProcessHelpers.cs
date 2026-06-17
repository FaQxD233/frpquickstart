using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace FrpQuickStart.Shared;

public static class ProcessHelpers
{
    public static string ResolveExecutable(string? configuredPath, string executableName, string? bundledResourceName = null)
    {
        if (IsExplicitExecutablePath(configuredPath, executableName))
        {
            return configuredPath!.Trim();
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

        if (!string.IsNullOrWhiteSpace(bundledResourceName))
        {
            var bundledPath = TryExtractBundledExecutable(bundledResourceName, fileName);
            if (bundledPath is not null)
            {
                return bundledPath;
            }
        }

        return configuredPath ?? executableName;
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
                $"找不到或无法执行 {executablePath}。程序会优先使用内置 frp 二进制；如需覆盖，请把对应二进制放到程序同目录、加入 PATH，或通过参数/配置指定路径。原始错误: {ex.Message}",
                ex);
        }
    }

    private static bool IsExplicitExecutablePath(string? configuredPath, string executableName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        var trimmedPath = configuredPath.Trim();
        if (Path.IsPathRooted(trimmedPath) ||
            trimmedPath.Contains(Path.DirectorySeparatorChar) ||
            trimmedPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        var configuredName = Path.GetFileName(trimmedPath);
        if (string.Equals(configuredName, executableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() &&
            string.Equals(configuredName, executableName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? TryExtractBundledExecutable(string resourceName, string fileName)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            return null;
        }

        var targetDirectory = Path.Combine(Environment.CurrentDirectory, "runtime", "frp");
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, fileName);
        var hashPath = targetPath + ".sha256";

        // BUG-7 FIX: 使用 SHA256 哈希校验替代仅比较文件大小
        // 先计算嵌入资源的哈希
        string resourceHash;
        using (var sha256 = SHA256.Create())
        {
            // 需要读取两次：一次计算哈希，一次写入文件，因此先缓存到内存
            using var ms = new MemoryStream();
            resourceStream.CopyTo(ms);
            var resourceBytes = ms.ToArray();
            resourceHash = Convert.ToHexString(sha256.ComputeHash(resourceBytes)).ToLowerInvariant();

            // 检查已提取文件是否匹配（大小 + 哈希双重校验）
            if (File.Exists(targetPath) && File.Exists(hashPath))
            {
                var existingHash = File.ReadAllText(hashPath, System.Text.Encoding.ASCII).Trim();
                if (string.Equals(existingHash, resourceHash, StringComparison.OrdinalIgnoreCase) &&
                    new FileInfo(targetPath).Length == resourceBytes.Length)
                {
                    EnsureExecutablePermission(targetPath);
                    return targetPath;
                }
            }

            // 提取到临时文件
            var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, resourceBytes);
                EnsureExecutablePermission(tempPath);

                // BUG-8 FIX: 使用 File.Move overwrite 直接覆盖，避免 delete+move 竞态窗口
                File.Move(tempPath, targetPath, overwrite: true);

                // 保存哈希值供下次对比
                File.WriteAllText(hashPath, resourceHash, System.Text.Encoding.ASCII);

                return targetPath;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* 临时文件清理失败不阻塞 */ }
                }
            }
        }
    }

    private static void EnsureExecutablePermission(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
