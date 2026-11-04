# dnSpyEx

dnSpyEx is an unofficial continuation of the [dnSpy](https://github.com/dnSpy/dnSpy) project which is a debugger and .NET assembly editor. You can use it to edit and debug assemblies even if you don't have any source code available. Main features:

- Debug .NET and Unity assemblies
- Edit .NET and Unity assemblies
- Decompile .NET assemblies to C#, with the ILSpy decompiler engine
- Light and dark themes

See below for more features

![debug-animated](images/debug-animated.gif)

![edit-code-animated](images/edit-code-animated.gif)

## Binaries

Latest stable release: https://github.com/dnSpyEx/dnSpy/releases

If you like living on the edge you can use the latest "beta" builds from:
[![](https://github.com/dnSpyEx/dnSpy/workflows/GitHub%20CI/badge.svg)](https://github.com/dnSpyEx/dnSpy/actions)

## Building

```PS
git clone --recursive https://github.com/dnSpyEx/dnSpy.git
cd dnSpy
# or dotnet build
./build.ps1 -NoMsbuild
```

To debug Unity games, you need this repo too: https://github.com/dnSpyEx/dnSpy-Unity-mono

The build needs the .NET 10 SDK. `build.ps1` uses MSBuild from Visual Studio unless you pass `-NoMsbuild`.

### Tests

```PS
dotnet test Tests/dnSpy.Decompiler.ILSpy.Tests
```

The tests decompile, check the debug info used for breakpoints and stepping, edit and export code and compile and run it, and decompile BAML. Pull requests also run a decompiler regression check that compares the decompiled output of a fixed corpus with the target branch's: see [Tests/RegressionCorpus](Tests/RegressionCorpus/README.md).

UI tests that start dnSpy and use it like a person would (Edit Method, debugging, decompiling, Export to Project) are in [Tests/dnSpy.UITests](Tests/dnSpy.UITests/README.md). They need Windows and a dnSpy build, and run on a hidden desktop.

# Decompiler

- C# decompiler: the unmodified [ILSpy](https://github.com/icsharpcode/ILSpy) engine (the `ICSharpCode.Decompiler` NuGet package), with C# up to 14: records, primary constructors, collection expressions, `required` members, extension members, `async` streams, and more
- IL disassembler, and "IL with C#": IL with each C# statement above its instructions
- Debug decompiled code without a PDB: breakpoints and stepping follow the decompiled statements, and locals have the names shown in the code
- Export to project: SDK-style or classic C# projects, `.sln` or `.slnx` solutions (Visual Studio 2005-2026), XAML from BAML, Windows Forms designer files and resources
- dnSpy's debug builds also have ILAst views and the ILAst stepper, which shows the decompiler's transforms one at a time

# Debugger

- Debug .NET Framework, .NET and Unity game assemblies, no source code required
- Set breakpoints and step into any assembly
- Locals, watch, autos windows
- Variables windows support saving variables (eg. decrypted byte arrays) to disk or view them in the hex editor (memory window)
- Object IDs
- Multiple processes can be debugged at the same time
- Break on module load
- Tracepoints and conditional breakpoints
- Export/import breakpoints and tracepoints
- Optional Just My Code (JMC) stepping filters for system libraries
- Call stack, threads, modules, processes windows
- Break on thrown exceptions (1st chance)
- Variables windows support evaluating C# expressions
- Dynamic modules can be debugged (but not dynamic methods due to CLR limitations)
- Output window logs various debugging events, and it shows timestamps by default :)
- Assemblies that decrypt themselves at runtime can be debugged, dnSpy will use the in-memory image. You can also force dnSpy to always use in-memory images instead of disk files.
- Bypasses for common debugger detection techniques
- Public API, you can write an extension or use the C# Interactive window to control the debugger

# Assembly Editor

- All metadata can be edited
- Edit methods and classes in C# with IntelliSense, no source code required
- Add new methods, classes or members in C#
- IL editor for low-level IL method body editing
- Low-level metadata tables can be edited. This uses the hex editor internally.

### Known limitations

Edit Method and Edit Class compile the decompiled C#, so they can only do what the decompiler can turn back into equivalent C# that compiles:

- **Obfuscated events**: in an assembly whose control flow was flattened or had dead code added (what ConfuserEx and similar obfuscators do), the decompiler may not recognize a field-like event. Editing a method that raises it can then give code that compiles but doesn't raise the event: the event's field gets another name than the event, or the `?.Invoke` is left out. Check the code before compiling when the assembly is obfuscated. (`ObfuscationTests` in the test project.)
- Members an obfuscator renamed to names that aren't C# identifiers don't compile.
- File-local types (C# 11), and code that uses them, have their metadata names and don't compile.
- Lambdas with default or `params` parameters get the compiler's generated delegate types and don't compile.
- Async methods with another builder than their return type's (`[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]`, e.g. in Npgsql) aren't decompiled: the decompiler shows the compiler's state machine, so neither the method nor its type compiles.
- A primary constructor whose base constructor arguments the decompiler shows as statements (e.g. a `?:` it shows as an `if`) has the compiler's names (`<value>P = value; ... base..ctor(...)`) and doesn't compile.
- A `with` expression on a struct can be shown as an assignment to the copy's init-only property (CS8852).
- `await foreach` over `ConfigureAwait`/`WithCancellation` (a `ConfiguredCancelableAsyncEnumerable<T>`) can be shown as `foreach`, and `Unsafe.SkipInit` on a ref struct (with `[SkipLocalsInit]`), which don't compile.
- A type named like a property of the type that uses it, where two imported namespaces have a type with that name (AutoMapper's `TypeConverter TypeConverter`), isn't qualified: CS0104.
- The decompiler finds a .NET Standard 2.0 library's packages that aren't part of .NET Standard (e.g. System.Reflection.Metadata) only next to it or in the GAC, else it shows their types unresolved (e.g. `Nullable` for a `CustomAttributeHandleCollection?`), which can make Edit Method's code not compile. (The edited code is compiled against the package's .NET Standard build in the NuGet package folders, else against the newest .NET.)
- Assemblies that the IL trimmer changed (.NET's own): a type whose constructors were removed can't be derived from in C#, so Edit Method of its derived types doesn't compile (System.Runtime.Intrinsics' `Avx2.X64`).

# Hex Editor

- Click on an address in the decompiled code to go to its IL code in the hex editor
- The reverse of the above, press F12 in an IL body in the hex editor to go to the decompiled code or other high-level representation of the bits. It's great to find out which statement a patch modified.
- Highlights .NET metadata structures and PE structures
- Tooltips show more info about the selected .NET metadata / PE field
- Go to position, file, RVA
- Go to .NET metadata token, method body, #Blob / #Strings / #US heap offset or #GUID heap index
- Follow references (Ctrl+F12)

# Other

- BAML decompiler and disassembler
- Blue, light and dark themes (and a dark high contrast theme)
- Bookmarks
- C# Interactive window can be used to script dnSpy
- Search assemblies for classes, methods, strings, etc
- Analyze class and method usage, find callers, etc
- Multiple tabs and tab groups
- References are highlighted, use Tab / Shift+Tab to move to the next reference
- Go to the entry point and module initializer commands
- Go to metadata token or metadata row commands
- Code tooltips

# List of other open source libraries used by dnSpy

- [ILSpy decompiler engine](https://github.com/icsharpcode/ILSpy) (C# decompiler, the unmodified ICSharpCode.Decompiler package)
- [Roslyn](https://github.com/dotnet/roslyn) (C# compiler)
- [dnlib](https://github.com/0xd4d/dnlib) (.NET metadata reader/writer which can also read obfuscated assemblies)
- [VS MEF](https://github.com/microsoft/vs-mef) (Faster MEF equals faster startup)
- [ClrMD](https://github.com/microsoft/clrmd) (Access to lower level debugging info not provided by the CorDebug API)
- [Iced](https://github.com/icedland/iced) (x86/x64 disassembler)
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (JSON serializer & deserializer)
- [NuGet.Configuration](https://github.com/NuGet/NuGet.Client) (NuGet configuration file reader)

# Translating dnSpy

[Click here](https://crowdin.com/project/dnspy) if you want to help with translating dnSpy to your native language.

# Wiki

See the [Wiki](https://github.com/dnSpyEx/dnSpy/wiki) for build instructions and other documentation.

# License

dnSpy is licensed under [GPLv3](dnSpy/dnSpy/LicenseInfo/GPLv3.txt).

# [Credits](dnSpy/dnSpy/LicenseInfo/CREDITS.txt)
