using System.Diagnostics;
using Microsoft.Win32;

namespace NetSpeed.Core;

/// <summary>
/// Logon autostart. A scheduled task with "highest privileges" is used when we are elevated so the
/// app can keep its ETW session without a UAC prompt on every boot; otherwise we fall back to the
/// classic Run key.
/// </summary>
public static class AutoStart
{
    private const string TaskName = "NetSpeed 网速监控";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetSpeed";

    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    public static bool IsEnabled => TaskExists() || RunKeyExists();

    public static bool Set(bool enable)
    {
        if (!enable)
        {
            RemoveTask();
            RemoveRunKey();
            return true;
        }

        if (EtwTrafficMonitor.IsElevated && CreateTask())
        {
            RemoveRunKey();
            return true;
        }

        RemoveTask();
        return CreateRunKey();
    }

    private static bool TaskExists() => RunSchTasks($"/Query /TN \"{TaskName}\"") == 0;

    private static bool CreateTask()
    {
        string cmd = $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15";
        if (RunSchTasks(cmd) == 0) return true;
        // /DELAY is not accepted on every build; retry without it.
        return RunSchTasks($"/Create /F /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --autostart\" /SC ONLOGON /RL HIGHEST") == 0;
    }

    private static void RemoveTask()
    {
        if (TaskExists()) RunSchTasks($"/Delete /F /TN \"{TaskName}\"");
    }

    private static int RunSchTasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private static bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        catch { return false; }
    }

    private static bool CreateRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key?.SetValue(RunValue, $"\"{ExePath}\" --autostart");
            return true;
        }
        catch { return false; }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) != null) key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { }
    }
}
