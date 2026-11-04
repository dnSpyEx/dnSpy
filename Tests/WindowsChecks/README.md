# Windows checks

`Run-WindowsChecks.ps1` runs the decompiler checks that need Windows: the WPF UI, MSBuild for WPF and WinForms
projects, the GAC and .NET Framework. CI runs the headless tests and the corpus check on every pull request
(`Tests/RegressionCorpus/README.md`); run this script by hand after a bigger `ICSharpCode.Decompiler` update.

## Running it

Use a PowerShell with the .NET 10 SDK, for example "Developer PowerShell for VS" (MSBuild from Visual Studio is used
when it's installed). In the repository:

```
powershell -ExecutionPolicy Bypass -File Tests\WindowsChecks\Run-WindowsChecks.ps1
```

It takes 15-60 minutes, depending on the machine. Leave the desktop alone during the last step: it starts dnSpy a few
times and takes screenshots. When it's done, it prints the path of
`Tests\WindowsChecks\out\WindowsChecks-<date-time>.zip`; `summary.md` in it has the results.

Nothing outside the repository changes: dnSpy is started with its own settings file, and exported projects are built
in a temporary folder that's deleted afterwards.

Steps (`-Skip Build,Corpus` or `-Only Samples` to run some of them; `-Skip Build` needs a Debug and Release build):

| Step | What |
|---|---|
| Build | `dnSpy.sln` Debug and Release (.NET Framework 4.8 and .NET 10), the test projects, `DebuggerPlayground` |
| Tests | The headless test suite (`Tests\dnSpy.Decompiler.ILSpy.Tests`) on Windows |
| Baml | BAML to XAML of dnSpy's own assemblies, the WPF framework (.NET 10) and .NET Framework WPF with its themes |
| Corpus | Decompiles 13 .NET Framework 4.8 assemblies (mscorlib, WinForms, WPF, ...) (`CorpusRunner`) |
| Samples | `Samples\*`: a console, a WPF, a WinForms and a .NET Framework app. Each is exported with `dnSpy.Console` (its .NET 10 and its .NET Framework build; SDK-style, and a classic project for .NET Framework), the exported project is built with MSBuild and run, and must print the same as the original |
| Screenshots | Starts dnSpy (Debug, .NET 10 and .NET Framework) on "C#", "IL with C#", ILAst with the stepper and typed IL, and takes screenshots. Also checks that a new settings file starts on "C#", and that the settings of dnSpy's old C# decompiler are moved to the new one |

The test project also uses `Samples\WpfSample`: it builds it and exports it.

## Other checks

- `Tests\dnSpy.UITests` drives the real dnSpy: Edit Method, debugging, decompiling, go to definition and Export to
  Project (`Tests\dnSpy.UITests\README.md`).
- Still manual: the debugger checklist in `Tests\DebuggerPlayground\README.md` (what the UI tests don't check), Unity
  debugging, and the code view's tooltips and highlighting, the Export to Project dialog and the ILAst stepper.
