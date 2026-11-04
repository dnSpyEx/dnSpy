# Decompiler regression check

dnSpy decompiles with the upstream ILSpy engine (the `ICSharpCode.Decompiler` package) behind its adapter
(`Extensions/ILSpy.Decompiler`). Dependabot opens pull requests for its new versions (`.github/dependabot.yml`; the
version is `ILSpyDecompilerVersion` in `DnSpyCommon.props`). Every pull request, including those, runs
`.github/workflows/decompiler-regression.yml`:

- **Tests** (Windows): builds `dnSpy.sln` (Debug, .NET Framework and .NET) and runs `Tests/dnSpy.Decompiler.ILSpy.Tests`.
  They decompile, check the debug info (breakpoints, stepping), edit and export code and compile and run it, decompile
  BAML, and check the .NET Framework build's binding redirects.
- **Corpus** (Linux): `Run-Corpus.ps1` decompiles a fixed corpus with the pull request and with its target branch:
  pinned NuGet packages (old and new C#, async, Span and unsafe code, IL emit, F#, crypto), every type, and synthetic samples
  with obfuscator tricks. It fails if the pull request is worse (a crash, fewer types decompiled, more exceptions,
  timeouts or debug info errors), and lists the types whose output changed.

## Reviewing an update

1. Both jobs pass.
2. The corpus job's summary (on the workflow run's page) says how many types' output changed. Download the
   `decompiler-regression` artifact and read `output.diff`: an update is expected to change some output. Look for
   output that got worse.
3. If the build fails:
   - **NU1605** (package downgrade): the new version needs newer `System.Reflection.Metadata` or
     `System.Collections.Immutable`. Raise `DotNetPackageVersion` in `DnSpyCommon.props`.
   - **API changes** in `ICSharpCode.Decompiler`: fix the adapter (`Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy.Core`).
     Don't fork or patch the package.
4. If `BindingRedirectTests` fails, update `dnSpy/dnSpy/app.config` as its message says.
5. For a bigger release, also run the UI tests (`Tests/dnSpy.UITests`) and `Tests/WindowsChecks/Run-WindowsChecks.ps1`
   on Windows (exported sample apps that are built and run, BAML from the GAC), and the debugger checklist in
   `Tests/DebuggerPlayground/README.md`.

## Roslyn

Roslyn isn't updated by Dependabot. dnSpy's debugger compiles expressions with Roslyn's expression evaluator, built
from source in `dnSpy/Roslyn/Roslyn.ExpressionCompiler` (dnSpyEx's fork, with dnSpy's changes for debugging decompiled
code). That source has to match the Roslyn version, and dnSpyEx updates the two together, with `app.config`'s
`Microsoft.CodeAnalysis*` binding redirects. Take Roslyn updates by merging dnSpyEx's master; the same CI runs on that
pull request.

## Running it locally

```
git worktree add ../dnspy-base origin/master
pwsh Tests/RegressionCorpus/Run-Corpus.ps1 -BaseRepo ../dnspy-base
```

The output is in `artifacts/regression`: `summary.md`, `output.diff`, and each run's `results.json`, `report.md` and
`dump/` (each type's output). Without `-BaseRepo` it only decompiles (and only crashes fail).

The corpus packages are pinned in `Run-Corpus.ps1` because a new version would change the output. They're downloaded
from nuget.org, not stored in the repository.
