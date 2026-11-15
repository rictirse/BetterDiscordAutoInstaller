using System.Diagnostics;

namespace BetterDiscordAutoInstaller.Utils;

public static class ProcessUtils
{
    public static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public static DateTime? GetEarliestStartTime(string processName)
    {
        DateTime? earliest = null;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                try
                {
                    var t = p.StartTime;
                    if (earliest == null || t < earliest) earliest = t;
                }
                catch
                {
                    // 權限不足或 process 已結束
                }
            }
        }
        return earliest;
    }
}
