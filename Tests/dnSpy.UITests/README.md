# dnSpy UI tests

Tests that start the real dnSpy and use it like a person would: open assemblies, switch languages, Edit Method and
Compile, save, debug (breakpoints, stepping, the call stack, Watch), export to a project. They check what dnSpy shows
and that it didn't throw or leave a dialog open.

They're separate from dnSpy: nothing in dnSpy, its projects or `dnSpy.sln` refers to them. To remove them, delete
`Tests\dnSpy.UITests` and `Tests\dnSpy.UITests.Probe`.

## Running

Build dnSpy first (Debug: `dnSpy\dnSpy\bin\Debug\net10.0-windows\dnSpy.exe`), then:

```
dotnet test Tests\dnSpy.UITests
```

The PC can be used while they run: dnSpy runs on a hidden Windows desktop, so its windows, dialogs and the programs it
debugs never appear on the screen or take the focus. A full run takes about 15–25 minutes; filter with e.g.
`--filter FullyQualifiedName~DebuggerTests`.

| Environment variable | |
|---|---|
| `DNSPY_UITEST_EXE` | the dnSpy.exe to test (default: the Debug build, or Release when the tests are built Release) |
| `DNSPY_UITEST_SAMPLES` | how many types/methods of a big assembly the stress tests use (default 30) |
| `DNSPY_UITEST_OUT` | where the run's files go (default `%TEMP%\dnSpy.UITests\<date-time>`): built samples, edited and exported files, screenshots |
| `DNSPY_UITEST_REAL_KEYBOARD=1` | also run `KeyboardShortcutTests` (see below) |

When a test fails because something didn't happen in time, the error has the paths of screenshots of dnSpy's windows
(rendered by the probe, so they work on the hidden desktop).

### Keyboard shortcut tests

`KeyboardShortcutTests` press dnSpy's shortcuts (F5, F9, F10, F11, Shift+F11, Shift+F5, Ctrl+Shift+E, Ctrl+Alt+C, F12,
Escape) with the real keyboard. They need dnSpy in the foreground of your desktop, so they're skipped unless
`DNSPY_UITEST_REAL_KEYBOARD=1`, and you can't use the PC while they run:

```
$env:DNSPY_UITEST_REAL_KEYBOARD = "1"; dotnet test Tests\dnSpy.UITests --filter FullyQualifiedName~KeyboardShortcutTests
```

## How it works

- `Tests\dnSpy.UITests.Probe` is a dnSpy extension. dnSpy only loads it because the tests start it with
  `--extension-directory`, and it does nothing unless `DNSPY_UITEST_PIPE` is set. It answers the tests over a named
  pipe: the active tab's text and caret, the Edit Code dialog's code and errors, the debugger's frames, locals and
  expression values, open windows, unhandled exceptions.
- Input goes through the probe too, inside dnSpy: key presses are WPF key events on the focused element with the
  modifier keys set in the UI thread's key state (what dnSpy's shortcut handlers and WPF's key bindings read), and
  buttons, check boxes and menu items are invoked through their automation peers (like UI Automation). So the tests
  don't need the focus, and the keyboard tests above check that the real keys get there.
- Each test starts with an empty dnSpy (one dnSpy for the whole run, started again if it crashed) with its own settings
  file, and fails if dnSpy threw an unhandled exception or a dialog is still open at the end.
- dnSpy and the programs it debugs are in a job object that Windows closes when the test host exits, so nothing is left
  running on the hidden desktop if a run is stopped.

## Known issues and limits

- A dnSpy bug the tests hit but don't test can be listed in `DnSpyApp.KnownIssues`: it's logged ("Known issue: ...")
  instead of failing the test. (An unobserved task's exception is raised when the GC runs, during whatever test is
  running then.) The list is empty.
- Edit Method skips the constructors the decompiler doesn't declare (an empty default constructor, a primary
  constructor whose body is only its parameters): there's no method at the caret in the code view, the tree view's
  context menu edits them.
- Watch: calling a method (a property getter too) at a method's first instruction or the start of a catch block can
  fail with 0x80131313 (the runtime can't run code there); the tests evaluate fields or stop after the first statement.
- Escape and Enter in dialogs are WPF access keys, which only real key presses trigger: the probe presses the Cancel or
  default button instead, and `KeyboardShortcutTests` checks Escape with the real keyboard.

## What's tested

- `DebuggerTests` (Debug and Release builds of `Tests\DebuggerPlayground`, no PDB: the decompiler's debug info):
  async Main's entry point (F10/F11 go into Main, IL view steps one instruction), breakpoints hit exactly when their
  statement runs (switch, break, continue, do-while, conditional, catch/finally, lambda, lock/using/foreach), the call
  stack (each caller frame shows its call), Watch with the types of the file's usings, locals in a catch block, stepping
  over awaits and lock/using/while/foreach, step into and step out, a step during an evaluation that loads assemblies
  and after one that timed out.
- `EditCodeTests`: Edit Method without changes on every method of a .NET 10 (C# 14) and a .NET Framework 4.8 sample and
  on a sample of Newtonsoft.Json (netstandard 2.0, nullable annotations); async Main's declaration; three edits in a row
  of a method with collection expressions; an added statement saved and run; a compiler error.
- `DecompileTests`: a sample of types of System.Private.CoreLib, Newtonsoft.Json and the samples in C#, IL and IL with C#
  with no errors; go to definition (same and another assembly) and back; switching languages; a tab closed while it
  decompiles.
- `ExportTests`: Export to Project of .NET 10 (always SDK-style, built and run) and .NET Framework (classic) assemblies.
