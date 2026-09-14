using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Agnes.Screenshots;

/// <summary>
/// Who is holding what: a full dump of this process at a chosen moment, and the GC root paths to every
/// instance of a type in it. The tool the leak in the cold tier was found with.
/// </summary>
/// <remarks>
/// <code>
/// dotnet run --project tools/Agnes.Screenshots -- --switch-timing --dump /tmp/agnes.dmp
/// dotnet run --project tools/Agnes.Screenshots -- --roots /tmp/agnes.dmp Agnes.Ui.Core.ViewModels.SessionViewModel
/// </code>
/// Yama's ptrace scope (1 on Ubuntu) lets only a descendant trace a process, and createdump is a child
/// tracing its parent; the runtime gets round that on a crash by allowing any tracer first, and so
/// does this.
/// </remarks>
public static class HeapDump
{
    private const int PR_SET_PTRACER = 0x59616d61;
    private const long PR_SET_PTRACER_ANY = -1;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, long arg2, long arg3, long arg4, long arg5);

    public static void WriteSelf(string path)
    {
        var createdump = Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "createdump").FirstOrDefault();
        if (createdump is null)
        {
            Console.WriteLine("no createdump beside the runtime; skipping the dump");
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            _ = prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0);
        }

        var run = Process.Start(new ProcessStartInfo(createdump, $"--full -f \"{path}\" {Environment.ProcessId}") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd();
        run.WaitForExit();
        Console.WriteLine($"dump: exit {run.ExitCode} → {path} {(File.Exists(path) ? new FileInfo(path).Length / 1048576 + " MB" : "(missing)")}");
        if (run.ExitCode != 0)
        {
            Console.WriteLine(output.Trim());
        }
    }

    public static (string dump, string type)? TryParseReferrers(string[] args)
    {
        var i = Array.IndexOf(args, "--referrers");
        return i >= 0 && i + 2 < args.Length ? (args[i + 1], args[i + 2]) : null;
    }

    /// <summary>Who points at each instance of a type, two hops up — independent of root kinds, so a
    /// holder behind a dependent handle (a ConditionalWeakTable) shows as well.</summary>
    public static void Referrers(string dump, string typeName)
    {
        using var target = DataTarget.LoadDump(dump);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var heap = runtime.Heap;
        var targets = heap.EnumerateObjects().Where(o => string.Equals(o.Type?.Name, typeName, StringComparison.Ordinal)).Select(o => o.Address).ToHashSet();
        Console.WriteLine($"{targets.Count} instance(s) of {typeName}");
        var wanted = new HashSet<ulong>(targets);
        var level = new Dictionary<ulong, List<(ulong from, string type)>>();
        for (var hop = 1; hop <= 3 && wanted.Count > 0; hop++)
        {
            var found = new Dictionary<ulong, List<(ulong, string)>>();
            foreach (var obj in heap.EnumerateObjects())
            {
                if (obj.Type is null || obj.Type.IsFree)
                {
                    continue;
                }
                foreach (var reference in obj.EnumerateReferences(carefully: false, considerDependantHandles: true))
                {
                    if (wanted.Contains(reference.Address))
                    {
                        if (!found.TryGetValue(reference.Address, out var list))
                        {
                            found[reference.Address] = list = [];
                        }
                        list.Add((obj.Address, obj.Type.Name ?? "?"));
                    }
                }
            }

            Console.WriteLine($"--- hop {hop}");
            var next = new HashSet<ulong>();
            foreach (var (address, holders) in found)
            {
                var holderTypes = holders.GroupBy(h => h.Item2).Select(g => $"{g.Key} ×{g.Count()}");
                Console.WriteLine($"  {heap.GetObjectType(address)?.Name} @ {address:x} ← {string.Join(", ", holderTypes)}");
                foreach (var h in holders.Where(h => !h.Item2.StartsWith("Agnes.Ui.Core.ViewModels.SessionViewModel", StringComparison.Ordinal)).Take(6))
                {
                    next.Add(h.Item1);
                }
            }
            wanted = next;
        }
    }

    public static (string dump, string type)? TryParseRoots(string[] args)
    {
        var i = Array.IndexOf(args, "--roots");
        return i >= 0 && i + 2 < args.Length ? (args[i + 1], args[i + 2]) : null;
    }

    public static void Roots(string dump, string typeName)
    {
        using var target = DataTarget.LoadDump(dump);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var heap = runtime.Heap;
        var targets = heap.EnumerateObjects().Where(o => string.Equals(o.Type?.Name, typeName, StringComparison.Ordinal)).ToList();
        Console.WriteLine($"{targets.Count} instance(s) of {typeName}");
        var gcroot = new GCRoot(heap);
        var seen = 0;
        foreach (var t in targets)
        {
            foreach (var rootPath in gcroot.EnumerateGCRoots(t.Address, CancellationToken.None))
            {
                seen++;
                Console.WriteLine($"--- root {rootPath.Root.RootKind} {rootPath.Root.Object.Type?.Name} @ {rootPath.Root.Object.Address:x}");
                if (rootPath.Root is ClrStackRoot stackRoot)
                {
                    var frame = stackRoot.StackFrame;
                    Console.WriteLine($"    on thread {frame?.Thread?.OSThreadId} in {frame?.Method?.Signature ?? frame?.FrameName ?? "?"}");
                    var thread = frame?.Thread;
                    if (thread is not null)
                    {
                        foreach (var f in thread.EnumerateStackTrace().Take(12))
                        {
                            Console.WriteLine($"       {f.Method?.Signature ?? f.FrameName}");
                        }
                    }
                }
                foreach (var hop in rootPath.Path.Take(40))
                {
                    Console.WriteLine($"    → {hop.Type?.Name}");
                }
                if (seen >= 6)
                {
                    break;
                }
            }
            if (seen >= 6)
            {
                break;
            }
        }
        if (seen == 0)
        {
            Console.WriteLine("no root path found (the instances are unreachable — a stale weak reference, or a GC had not run)");
        }
    }
}
