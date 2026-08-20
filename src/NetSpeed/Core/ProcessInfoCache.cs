using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using NetSpeed.Interop;

namespace NetSpeed.Core;

public sealed record ProcessInfo(string Key, string DisplayName, string ExeName, string ImagePath);

/// <summary>Maps PIDs to a display name, grouping by executable so Chrome's 30 helpers show as one row.</summary>
public static class ProcessInfoCache
{
    private sealed class PidEntry
    {
        public ProcessInfo Info = null!;
        public DateTime ResolvedAt;
    }

    private static readonly ConcurrentDictionary<int, PidEntry> ByPid = new();
    private static readonly ConcurrentDictionary<string, string> DescriptionByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PidTtl = TimeSpan.FromSeconds(20);

    private static readonly Dictionary<string, string> FriendlyOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost.exe"] = "Windows 服务主机",
        ["System"] = "系统内核",
        ["Registry"] = "系统注册表",
        ["MsMpEng.exe"] = "Microsoft Defender",
        ["SearchHost.exe"] = "Windows 搜索",
        ["backgroundTaskHost.exe"] = "后台任务宿主",
        ["ApplicationFrameHost.exe"] = "应用框架宿主",
        ["dllhost.exe"] = "COM 代理宿主",
        ["taskhostw.exe"] = "任务宿主",
    };

    public static ProcessInfo Resolve(int pid)
    {
        if (pid == 4) return new ProcessInfo("::system", "系统内核", "System", string.Empty);
        if (pid <= 0) return new ProcessInfo("::unknown", "未知进程", string.Empty, string.Empty);

        if (ByPid.TryGetValue(pid, out var cached) && DateTime.UtcNow - cached.ResolvedAt < PidTtl)
            return cached.Info;

        var info = ResolveCore(pid);
        ByPid[pid] = new PidEntry { Info = info, ResolvedAt = DateTime.UtcNow };

        if (ByPid.Count > 4096) ByPid.Clear();
        return info;
    }

    private static ProcessInfo ResolveCore(int pid)
    {
        string path = QueryImagePath(pid);

        if (string.IsNullOrEmpty(path))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string n = p.ProcessName + ".exe";
                return new ProcessInfo("name::" + n.ToLowerInvariant(), FriendlyName(n, null), n, string.Empty);
            }
            catch
            {
                return new ProcessInfo("pid::" + pid, $"进程 {pid}", string.Empty, string.Empty);
            }
        }

        string exe = Path.GetFileName(path);
        return new ProcessInfo(path.ToLowerInvariant(), FriendlyName(exe, path), exe, path);
    }

    private static string QueryImagePath(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return string.Empty;
        try
        {
            int size = 1024;
            var sb = new StringBuilder(size);
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        catch { return string.Empty; }
        finally { Native.CloseHandle(h); }
    }

    private static string FriendlyName(string exe, string? path)
    {
        if (FriendlyOverrides.TryGetValue(exe, out var forced)) return forced;

        if (!string.IsNullOrEmpty(path))
        {
            var desc = DescriptionByPath.GetOrAdd(path, static p =>
            {
                try
                {
                    var d = FileVersionInfo.GetVersionInfo(p).FileDescription;
                    return string.IsNullOrWhiteSpace(d) ? string.Empty : d.Trim();
                }
                catch { return string.Empty; }
            });
            if (!string.IsNullOrEmpty(desc)) return desc;
        }

        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exe[..^4]
            : exe;
    }
}
