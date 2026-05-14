using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace dnSpy.Extension.MalwareMCP.Tools;

/// <summary>
/// Debugger control tools — implemented via reflection against dnSpy.Contracts.Debugger so
/// that this extension does not need a hard build-time reference (which would require
/// an extra DLL in lib\). These tools work whenever dnSpy is running with its debugger
/// extension loaded (i.e., always, in a normal dnSpy install).
/// </summary>
[McpServerToolType]
public static class DebuggerTools
{
    static readonly object _gate = new();
    static object? _dbgManager;
    static Type? _dbgManagerType;
    static bool _initTried;

    static object? GetDbgManager(DnSpyBridge bridge)
    {
        lock (_gate)
        {
            if (_dbgManager != null) return _dbgManager;
            if (_initTried) return null;
            _initTried = true;

            // Find dnSpy.Contracts.Debugger.dll among loaded assemblies
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "dnSpy.Contracts.Debugger");
            if (asm == null) return null;

            _dbgManagerType = asm.GetType("dnSpy.Contracts.Debugger.DbgManager");
            if (_dbgManagerType == null) return null;

            // dnSpy uses MEF; we can grab the singleton via the IAppWindow's MEF container.
            // The supported way is to import it; absent that, try reflection on the global container.
            try
            {
                var appWindowType = bridge.AppWindow.GetType();
                // IAppWindow has no public MEF accessor in 6.5, but most containers expose ExportProvider.
                // Walk fields/properties looking for an ExportProvider.
                var ep = FindExportProvider(bridge.AppWindow);
                if (ep == null) return null;

                var getExported = ep.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetExportedValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getExported == null) return null;

                _dbgManager = getExported.MakeGenericMethod(_dbgManagerType).Invoke(ep, null);
                return _dbgManager;
            }
            catch
            {
                return null;
            }
        }
    }

    static object? FindExportProvider(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0 && visited.Count < 200)
        {
            var cur = stack.Pop();
            if (cur == null || !visited.Add(cur)) continue;
            var t = cur.GetType();
            if (t.FullName?.Contains("ExportProvider") == true)
                return cur;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                try { var v = p.GetValue(cur); if (v != null && !v.GetType().IsValueType) stack.Push(v); } catch { }
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try { var v = f.GetValue(cur); if (v != null && !v.GetType().IsValueType) stack.Push(v); } catch { }
            }
        }
        return null;
    }

    [McpServerTool, Description("Report the current debugger state (running/paused/stopped, processes, threads, current frame).")]
    public static object DebuggerStatus(DnSpyBridge bridge)
    {
        var dm = GetDbgManager(bridge);
        if (dm == null) return new { available = false, message = "Debugger contracts not available — is the debugger extension loaded?" };

        try
        {
            var processes = (System.Collections.IEnumerable?)dm.GetType().GetProperty("Processes")?.GetValue(dm);
            var procList = new List<object>();
            if (processes != null)
            {
                foreach (var p in processes)
                {
                    var pid = p.GetType().GetProperty("Id")?.GetValue(p);
                    var name = p.GetType().GetProperty("Name")?.GetValue(p);
                    var state = p.GetType().GetProperty("State")?.GetValue(p)?.ToString();
                    var runtimes = (System.Collections.IEnumerable?)p.GetType().GetProperty("Runtimes")?.GetValue(p);
                    var threadCount = 0;
                    if (runtimes != null)
                        foreach (var r in runtimes)
                            threadCount += ((System.Collections.ICollection?)r.GetType().GetProperty("Threads")?.GetValue(r))?.Count ?? 0;
                    procList.Add(new { pid, name, state, thread_count = threadCount });
                }
            }
            var isDebugging = (bool?)dm.GetType().GetProperty("IsDebugging")?.GetValue(dm) ?? false;
            return new { available = true, is_debugging = isDebugging, processes = procList };
        }
        catch (Exception ex)
        {
            return new { available = true, error = ex.Message };
        }
    }

    [McpServerTool, Description("Continue execution of all paused processes.")]
    public static object Continue(DnSpyBridge bridge) => InvokeOnDbgManager(bridge, "Run");

    [McpServerTool, Description("Break (pause) all running debuggee processes.")]
    public static object Break(DnSpyBridge bridge) => InvokeOnDbgManager(bridge, "BreakAll");

    [McpServerTool, Description("Stop debugging (terminate the debuggee).")]
    public static object Stop(DnSpyBridge bridge) => InvokeOnDbgManager(bridge, "StopDebuggingAll");

    [McpServerTool, Description("Step into the next instruction in the current thread.")]
    public static object StepInto(DnSpyBridge bridge) => StepCurrent(bridge, "StepInto");

    [McpServerTool, Description("Step over the next instruction in the current thread.")]
    public static object StepOver(DnSpyBridge bridge) => StepCurrent(bridge, "StepOver");

    [McpServerTool, Description("Step out of the current method.")]
    public static object StepOut(DnSpyBridge bridge) => StepCurrent(bridge, "StepOut");

    static object InvokeOnDbgManager(DnSpyBridge bridge, string methodName)
    {
        var dm = GetDbgManager(bridge);
        if (dm == null) return new { ok = false, error = "Debugger not available" };
        try
        {
            var mi = dm.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (mi == null) return new { ok = false, error = $"DbgManager has no method '{methodName}'" };
            mi.Invoke(dm, null);
            return new { ok = true, action = methodName };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    static object StepCurrent(DnSpyBridge bridge, string stepKind)
    {
        var dm = GetDbgManager(bridge);
        if (dm == null) return new { ok = false, error = "Debugger not available" };
        try
        {
            // Find current thread → runtime → DbgStepper.Step(kind)
            var processes = (System.Collections.IEnumerable?)dm.GetType().GetProperty("Processes")?.GetValue(dm);
            if (processes == null) return new { ok = false, error = "No processes" };
            object? thread = null;
            foreach (var p in processes)
            {
                var runtimes = (System.Collections.IEnumerable?)p.GetType().GetProperty("Runtimes")?.GetValue(p);
                if (runtimes == null) continue;
                foreach (var r in runtimes)
                {
                    var threads = (System.Collections.IEnumerable?)r.GetType().GetProperty("Threads")?.GetValue(r);
                    if (threads == null) continue;
                    foreach (var t in threads) { thread = t; break; }
                    if (thread != null) break;
                }
                if (thread != null) break;
            }
            if (thread == null) return new { ok = false, error = "No active thread" };

            var runtime = thread.GetType().GetProperty("Runtime")?.GetValue(thread);
            var createStepper = runtime?.GetType().GetMethod("CreateStepper", new[] { thread.GetType() });
            var stepper = createStepper?.Invoke(runtime, new[] { thread });
            if (stepper == null) return new { ok = false, error = "Could not create stepper" };

            var stepKindEnum = stepper.GetType().Assembly.GetType("dnSpy.Contracts.Debugger.DbgStepKind");
            if (stepKindEnum == null) return new { ok = false, error = "DbgStepKind not found" };
            var kindValue = Enum.Parse(stepKindEnum, stepKind);
            var step = stepper.GetType().GetMethods().FirstOrDefault(m => m.Name == "Step" && m.GetParameters().Length >= 1);
            if (step == null) return new { ok = false, error = "Step method not found" };
            var args = step.GetParameters().Length == 1
                ? new object?[] { kindValue }
                : new object?[] { kindValue, true };
            step.Invoke(stepper, args);
            return new { ok = true, action = stepKind };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();
    bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
    int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
