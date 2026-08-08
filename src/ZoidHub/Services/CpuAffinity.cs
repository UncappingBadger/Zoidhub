using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace ZoidHub.Services;

/// <summary>Keeps ZoidHub (both its own UI process and the render subprocess tree) off a
/// guaranteed-free slice of CPU cores, so Project Zomboid always has uncontested room regardless
/// of what ZoidHub is doing - same principle as PalHub's PinToDedicatedCores, extended to cover a
/// real subprocess tree (pzmap2dzi's render workers are actual OS processes, not just threads,
/// so a single call on the launched process doesn't reach them - affinity isn't inherited by
/// child processes on Windows).
///
/// Deliberately does not touch Project Zomboid's own affinity - only restricts ZoidHub's side,
/// same as PalHub's reasoning.</summary>
public static class CpuAffinity
{
    /// <summary>Cores permanently off-limits to any ZoidHub-related process. Matches PalHub's
    /// exact thresholds for consistency across this user's apps: 2 reserved on 4+-core machines,
    /// 1 on 2-3 core machines, 0 (untouched) below that - a scheduling hint must never make a
    /// small machine unusable.</summary>
    public static int ReservedForOtherApps
    {
        get
        {
            var cores = Environment.ProcessorCount;
            if (cores >= 4) return 2;
            if (cores >= 2) return 1;
            return 0;
        }
    }

    /// <summary>How many cores ZoidHub (UI + render) is allowed to use at all.</summary>
    public static int AvailableCores => Math.Max(1, Environment.ProcessorCount - ReservedForOtherApps);

    /// <summary>The affinity mask covering exactly AvailableCores cores (the bottom ones,
    /// leaving the top ReservedForOtherApps cores untouched by anything ZoidHub starts).</summary>
    private static IntPtr BuildMask()
    {
        var reserved = ReservedForOtherApps;
        if (reserved == 0) return IntPtr.Zero; // 0 = "don't restrict" sentinel, see callers

        long mask = 0;
        for (var i = 0; i < AvailableCores; i++) mask |= 1L << i;
        return (IntPtr)mask;
    }

    /// <summary>Confines the CURRENT process to the shared available-cores set. Call once, early.</summary>
    public static void PinCurrentProcess()
    {
        try
        {
            var mask = BuildMask();
            if (mask == IntPtr.Zero) return;
            Process.GetCurrentProcess().ProcessorAffinity = mask;
        }
        catch
        {
            // Never let a scheduling hint prevent the app from actually running.
        }
    }

    /// <summary>Confines a process (by PID) plus every descendant process to the shared
    /// available-cores set, and drops them to BelowNormal priority. Python's multiprocessing
    /// workers spawn progressively as pzmap2dzi ramps up, not all at once, so this is meant to be
    /// called a few times over the seconds after launch rather than just once - see
    /// MapRenderService's caller for the actual schedule.</summary>
    public static void PinProcessTree(int rootPid)
    {
        try
        {
            var mask = BuildMask();
            if (mask == IntPtr.Zero) return;

            foreach (var pid in FindProcessTreePids(rootPid))
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.ProcessorAffinity = mask;
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    // Process may have already exited between enumeration and here - fine, skip it.
                }
            }
        }
        catch
        {
            // Same principle as PinCurrentProcess - a scheduling hint is never allowed to break
            // the actual render.
        }
    }

    private static IEnumerable<int> FindProcessTreePids(int rootPid)
    {
        // Win32_Process.ParentProcessId is the only reliable way to walk a process tree from
        // .NET without P/Invoking NtQueryInformationProcess - System.Diagnostics.Process has no
        // concept of a parent PID at all.
        var childrenByParent = new Dictionary<int, List<int>>();
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject mo in results)
            {
                var pid = Convert.ToInt32(mo["ProcessId"]);
                var ppid = Convert.ToInt32(mo["ParentProcessId"]);
                if (!childrenByParent.TryGetValue(ppid, out var list))
                {
                    list = new List<int>();
                    childrenByParent[ppid] = list;
                }
                list.Add(pid);
            }
        }

        var toVisit = new Queue<int>();
        toVisit.Enqueue(rootPid);
        var seen = new HashSet<int> { rootPid };
        yield return rootPid;

        while (toVisit.Count > 0)
        {
            var current = toVisit.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var children)) continue;
            foreach (var child in children)
            {
                if (!seen.Add(child)) continue;
                toVisit.Enqueue(child);
                yield return child;
            }
        }
    }
}
