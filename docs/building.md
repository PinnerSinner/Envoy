# Building from source

## On Windows

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 2004 (19041) or later.
Visual Studio 2022 or Rider is optional.

```powershell
git clone <this repo>
cd envoy
dotnet test                                   # unit tests
dotnet run --project src/Envoy.App            # run the app (Debug)
dotnet publish src/Envoy.App -c Release -o publish
# → publish\Envoy.exe  (self-contained single file, ~75 MB, runs on any Windows 10/11 x64)
```

For a small exe that needs the .NET 8 Desktop Runtime installed:

```powershell
dotnet publish src/Envoy.App -c Release -o publish-fd -p:SelfContained=false
```

To run the UI smoke test locally:

```powershell
publish\Envoy.exe --smoke-test smoke.log; Get-Content smoke.log
```

It opens every settings page and window under every palette, checks commands, auto-apply and the live-view lease, and
fails on any WPF binding error. Each section logs `[smoke] <section> ok elapsed_ms=<n>`. Add
`--screenshots <folder>` to save a PNG of every page and window.

## On Linux or macOS

The core library and its tests are cross-platform:

```bash
dotnet test tests/Envoy.Core.Tests
```

The WPF app can't be *built* off Windows, because the XAML compiler ships only with the Windows SDK. You can still
**type-check** it:

```bash
tools/typecheck/typecheck.sh
```

This first runs the tool's own Python tests, then generates stand-ins for the XAML-generated code and compiles the
app's C# against the Windows reference assemblies with warnings as errors. The stand-ins carry each XAML root's base
class and a `nameof` for every event handler the XAML names, so a `<UserControl>` paired with `: Window`, or a
`Click="OnFoo"` with no `OnFoo`, fails here rather than on Windows. It also checks that every
`StaticResource`/`DynamicResource` key exists, that all palettes define the same keys, and the UI-structure rules in
`check_ui_rules.py` (no handlers or `DataContext` in `Pages/`, no `MessageBox` outside `DialogService`, and so on). CI
runs the real Windows build and smoke test on every push.

## Project settings worth knowing

- `Envoy.App.csproj` turns on `PublishSingleFile`, `SelfContained` and compression for Release builds, and embeds
  debug symbols.
- `app.manifest` declares Per-Monitor-V2 DPI awareness (capture regions are in physical pixels) and `asInvoker`
  (never elevate).
- `AllowUnsafeBlocks` is required by the `LibraryImport` P/Invoke source generator in `Interop/Native.cs`.
- WPF projects drop `System.IO` and `System.Net.Http` from implicit usings; `GlobalUsings.cs` adds them back.
