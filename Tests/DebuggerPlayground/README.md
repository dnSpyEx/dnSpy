# Debugger playground

A small program for checking dnSpy's debugger on decompiled code by hand, on Windows. Each scenario is a case
where the upstream decompiler's debug info sent breakpoints, stepping or the call stack to the wrong code before
dnSpy's adapter fixed it. The same program also runs in the automated stepping tests
(`Tests/dnSpy.Decompiler.ILSpy.Tests/SteppingTests.cs`), which replay dnSpy's stepper over its execution trace on
Linux, so this checklist is about what only the real debugger can show: the UI, the JIT and CorDebug.

## Setup

1. Build it: `dotnet build Tests\DebuggerPlayground -c Release` (also try `-c Debug` once; optimized code is the
   harder case). There's no PDB on purpose, so dnSpy uses the decompiler's debug info.
2. In dnSpy, select the **C#** language and open
   `Tests\DebuggerPlayground\bin\Release\net10.0\DebuggerPlayground.dll`.
3. Debug → Start Debugging (F5) → Executable: the `DebuggerPlayground.exe` next to the dll. Set breakpoints in
   `DebuggerPlayground.Scenarios` before starting, or start with "Break at: Entry point".

## Scenarios (method in `Scenarios`: what to do, what must happen)

| # | Method | Do | Expected |
|---|---|---|---|
| 1 | `Switch` | Breakpoint on `return 10;` | Stops once (value 1), not for 2 or 7 |
| 2 | `Break` | Breakpoint on `break;` | Stops once (the empty string), not on every iteration |
| 3 | `Continue` | Breakpoint on `sum += values[i];` (or `num += ...`) | Stops for 1 and 3, not for 20 |
| 4 | `DoWhile` | Breakpoint on `return n;` | Stops once |
| 5 | `Raise`, `Set` | Breakpoint on the statement, then F10 | The step goes to the closing brace with the statement done (`value` assigned), not to `}` in the middle of it. In Release, `Raise` returns to `Main` instead: nothing handles `Changed`, and the optimized code returns from the middle of the statement (with the compiler's PDB too) |
| 6 | `Conditional` | Breakpoint on `Console.Write("");` | Stops for both calls (the decompiled `if`/`?:` shape can differ from the source) |
| 7 | `Catch` | Breakpoint in the catch block | `ex` is in Locals and hovering `ex` shows the IOException |
| 8 | `Captured` | Breakpoint on `return f(...)` / `return func(...)`, Watch `captured` | Shows 3 |
| 9 | `Name` property | Breakpoints on `get;` and `set;` | Both are hit |
| 10 | `ComputeAsync` | Breakpoint on the first statement, F10 repeatedly | Each await steps to the next statement (also across `await Task.Delay`/`Task.Yield`, which resume on another thread) |
| 11 | `ThrowAsync`/`InnerAsync` | Debug → Windows → Exception Settings: break when `InvalidOperationException` is thrown | The `InnerAsync` frame highlights the `throw` |
| 12 | `Resources` | F10 through it | Steps through `lock`, `using`, the `while` and the `foreach`; no stop on a line that doesn't run |
| 13 | `Outer`/`Middle`/`Inner` | Break when `ArgumentException` is thrown, double-click the frames in the Call Stack | `Inner` highlights the `throw`, `Middle` highlights `list.ForEach(...)`, `Outer` its call |

Also worth a look while stopped: the Locals window shouldn't list temporaries like `V_3` that aren't in the code,
and switching the language to IL and back while stopped should keep the current statement highlighted.

Known differences from a compiler PDB (the automated tests measure them):

- Some calls have no statement, so a frame stopped in them shows no current statement: the base constructor call
  of a constructor that only initializes fields (`Scenarios` has none in the decompiled code, its `Name = ""`
  initializer is on the property) and, in `Conditional`, one arm of the conditional expression that the decompiler
  made of the else-if.
- In optimized code the decompiler can show a value that the IL keeps on the evaluation stack as a variable, e.g.
  `func` in `Captured`. A breakpoint on such a statement never hits: the JIT has no place to stop there (the
  compiler's own PDB has one statement for all of that code).
