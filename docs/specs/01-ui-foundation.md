# UI foundation: settings pages as UserControls, commands, and a typecheck that understands them

Status: Implemented (revised against `00-integration.md`; see Deviations)

Area id: `01-ui-foundation` · Requirement prefix: `UIF` · Step **1** of 11 (contract §1). It runs first and may assume
only today's code. The change must not alter behaviour: the screenshots and settings file stay the same, and every
button does what it does today.

This spec follows `docs/specs/00-integration.md` (the contract). Where they differ, the contract wins. This area
delivers the following for later steps (contract §1, row 1):

- `Pages/*` and `PageIds`;
- the `MainViewModel.*` partial files;
- `Envoy.Core.Mvvm`;
- `IShell` with the 01 block only;
- `DialogService`;
- `CapturePreviewViewModel` and `ChatWatcher.BeginPreview()`;
- `HotkeyRegistrationResult` and `AppHost.HotkeyResults`;
- the three-phase smoke test that loops over `ThemeManager.PaletteIds` × `PageIds.All`;
- the `build.yml` smoke timeout `WaitForExit(600000)`.

## Context

### What exists today

- `src/Envoy.App/Windows/MainWindow.xaml` (794 lines) holds the window chrome, the sidebar and **all nine settings
  pages** inline. Each page is a `ScrollViewer` whose `Visibility` is bound with `ElementName` to one sidebar
  `RadioButton` (`NavDashboard`, `NavTranslation`, `NavCapture`, `NavOverlay`, `NavReply`, `NavPhrases`, `NavHotkeys`,
  `NavAppearance`, `NavAbout`). Page boundaries are only comment banners:

  | Page (nav name) | XAML lines | Click / event handlers used |
  | --- | --- | --- |
  | War Room (`Dashboard`) | 69–217 | `OnDownloadUpdate`, `OnSelectRegion`, `OnToggleCapture`, `OnDemo`, `OnEditOverlay`, `OnCompose`, `OnScan`, `OnNewMatch` |
  | Translation | 219–371 | `OnLink` ×2 (Hyperlink `RequestNavigate`), `OnTestTranslate`; named `TestInput`, `TestOutput` |
  | Reading Chat (`Capture`) | 373–471 | `OnSelectRegion`, `OnInstallOcr` (inside a `DataTemplate`, `Tag="{Binding Code}"`); named `PreviewStats`, `PreviewImage`, `PreviewText` |
  | Overlay | 473–577 | `OnEditOverlay`, `OnDemo`, `OnTestVoice` |
  | Replying (`Reply`) | 579–635 | — |
  | Phrasebook (`Phrases`) | 637–672 | `OnEditPhrases`, `OnPhrasePreviewChanged`; named `PhrasePreviewLanguage`, `PhraseList` |
  | Hotkeys | 674–715 | — |
  | Appearance | 717–760 | `OnOpenLogs` |
  | About | 762–791 | `OnOpenData`, `OnQuit` |

- `src/Envoy.App/Windows/MainWindow.xaml.cs` (231 lines) contains every handler above, plus the following:
  - **Auto-apply.** It listens for bubbling `TextChanged`, `SelectionChanged`, `Checked`, `Unchecked`, `ValueChanged`
    and `PasswordChanged` events and restarts a 400 ms `DispatcherTimer`. The timer calls `ApplyNow()`, which runs
    `AppHost.ApplySettings()`, `RefreshStatus()` and `RefreshProviderSections()`. Controls that are not settings are
    excluded by **checking the instance** (`fe == TestInput || fe == PreviewText || fe == PhrasePreviewLanguage`) or by
    `RadioButton { GroupName: "Nav" }`.
  - **Live capture preview.** `NavCapture.Checked`/`Unchecked` sets the single bool `Watcher.PublishFrames`. `OnFrame`
    builds a `BitmapSource` into `PreviewImage`.
  - **Phrasebook preview.** `FillPhrases()` projects `PhraseBook.All` into a nested `PhraseRow` record. `OnEditPhrases`
    writes a template `phrases.json`, opens it, blocks on a `MessageBox`, then reloads.
  - **OCR language install.** `OnInstallOcr` builds a PowerShell `Add-WindowsCapability` command line inline. It
    special-cases `zh-CN`/`zh-TW`, which use the code instead of the OCR tag, and calls `Process.Start` with
    `Verb=runas`.
  - **Test translation.** `OnTestTranslate` is `async void`. It flushes pending edits and formats the result string
    inline.
  - **Page names.** `SmokeTest.cs` uses `PageNames` (`Dashboard`, `Translation`, `Capture`, `Overlay`, `Reply`,
    `Phrases`, `Hotkeys`, `Appearance`, `About`) and `Navigate(string)`.
  - **On close.** `OnClosed` flushes a pending apply, stops the preview, sets `FirstRunCompleted = true` and saves.
- `src/Envoy.App/ViewModels/MainViewModel.cs` (270 lines) holds one class plus `Option`, `OcrLanguageItem` and
  `ThemeOption`. It has properties but no commands. It reaches `App.Current.HotkeyErrors` directly. That list holds
  `"Name (combo)"` strings, written by `App.RegisterHotkeys` for every combo that fails to parse or register.
- `MessageBox.Show` appears in three places: `MainWindow.xaml.cs:137` (Edit phrases), `ComposeWindow.xaml.cs:212`
  (send failure) and `App.xaml.cs:182` (unhandled exception). `Process.Start` appears in `App.OpenUrl` and
  `MainWindow.OnInstallOcr`.
- `src/Envoy.App/SmokeTest.cs` loops over `ThemeManager.Themes` (5). For each theme it creates `MainWindow` and
  navigates every page. With `--screenshots` it saves `{theme}-settings-{dashboard,translation,capture,appearance}.png`,
  then builds the overlay, compose, lookup and region-selector windows. The compose step waits a fixed 2.5 s for each
  theme. `build.yml` waits `WaitForExit(180000)`. CI publishes the screenshots to the `ci-screenshots` branch, and the
  README and docs images come from there.
- `tools/typecheck/gen_stubs.py` writes a `partial class` stub for each `x:Class` XAML file. Each stub has an empty
  `InitializeComponent()` and one `internal` field per `x:Name` outside templates. It has these limits:
  - It emits **no base class**. A root element that disagrees with the code-behind (`<UserControl>` vs `: Window`)
    passes on Linux and fails only on Windows CI.
  - It writes `stubs/<ShortName>.g.cs`, so two classes with the same short name overwrite each other silently.
  - It ignores `x:FieldModifier`.
  - It does **not check event-handler names**. A `Click="OnFoo"` with no matching `OnFoo` passes the typecheck and
    only breaks the Windows build.

### What is weak for production

1. Every later area edits the same 800-line XAML file and the same code-behind, so merges conflict and review is hard.
2. Button logic lives in view code-behind, where it can't be tested. An `async void` handler crashes the app on an
   unexpected exception. `OnTestTranslate` happens to catch its own exceptions, but nothing enforces that.
3. Some small but real logic sits in the App project, where Linux CI cannot test it: the OCR-pack command line, the
   phrase preview projection, muted-player text parsing and the test-translation text.
4. The auto-apply exclusion list works by field identity. Once controls move into separate pages, those fields no
   longer exist on `MainWindow`.
5. CI does not fail on WPF binding errors. Splitting XAML is exactly the kind of change that breaks bindings silently,
   for example an `ElementName` across a namescope or a wrong `DataContext`.
6. Two preview consumers (this area's Reading Chat page and 07a's guide) would fight over one bool flag.
7. Hotkey failures exist only as display strings on `App`, so 02's diagnostics and 07a's hints have nothing typed to
   read.

## Goals / Non-goals

**Goals**

- One `UserControl` per settings page under `src/Envoy.App/Pages/`. All pages share the single `MainViewModel`
  instance that `MainWindow` creates, and `MainViewModel` is split into per-page partial files.
- A small command toolkit in `Envoy.Core.Mvvm` (`RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand`,
  `ObservableObject`), with unit tests. Page buttons and hyperlinks run through commands on the view model.
- The logic hidden in `MainWindow.xaml.cs` moves into `Envoy.Core`, with tests. Behaviour stays byte-identical, and
  the text moves into Core `*Text` classes (contract §8.7).
- The contract's 01 seams: the `IShell` 01 block, `DialogService`, the preview lease, and `AppHost.HotkeyResults`.
- The typecheck stub generator understands UserControls, base classes and event-handler names. A small
  `check_ui_rules.py` enforces the structural rules mechanically.
- The smoke test takes the contract §6 three-phase shape. It checks that every page loads, fails on binding errors,
  covers auto-apply and the preview lease, and fits the 600 s budget.
- A pixel-identical look: same window size, layout, styles, palette keys and screenshot file names.

**Non-goals**

- No visual redesign, new settings, new pages, new interactive controls, or changes to text or wording.
- No MVVM framework dependency (CommunityToolkit.Mvvm, Prism and so on). The toolkit stays about 150 lines.
- No splitting of `MainViewModel` into several view-model classes. It stays one class in partial files, plus
  `CapturePreviewViewModel`.
- No conversion of `OverlayWindow`, `ComposeWindow`, `LookupWindow` or `RegionSelectorWindow` to commands.
- No change to `AppSettings`, the settings JSON or the threading of the capture loop. `AppHost` changes only by the
  `HotkeyResults` property and event. `ChatWatcher` changes only by `PublishFrames` → `BeginPreview()`. `OnStartup`
  and `ExitApp` ordering are unchanged; 02 owns the §4 skeleton.
- Nothing from a later step: no `AppHostOptions`, `ILog`, `ShellLauncher`, `ProviderHealth*`, notices, or
  `HotkeyStatus.SkippedSafeMode` (02 adds it). There are no placeholder members (contract §1 arrival rule).
- No CI restructure. The only `build.yml` change is the smoke `WaitForExit` value.
- No lazy page loading or virtualisation. Pages are created eagerly, as the inline XAML is today.

## Requirements

MUST count: **15**, matching contract §9 (UIF-1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 13, 15, 18, 19, 21). Ids keep their
original numbers. Folded ids stay listed so references from other specs still resolve.

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-UIF-1 | MUST | `MainWindow.xaml` contains only the window chrome, the sidebar navigation and nine page host elements. Each page's content lives in its own `UserControl` in namespace `Envoy.App.Pages` under `src/Envoy.App/Pages/`: `WarRoomPage`, `TranslationPage`, `ReadingChatPage`, `OverlayPage`, `ReplyingPage`, `PhrasebookPage`, `HotkeysPage`, `AppearancePage`, `AboutPage`. `MainViewModel` is split into the partial files listed in Design §3, and `Option`, `OcrLanguageItem` and `ThemeOption` move to their own files (absorbs REQ-UIF-22). |
| REQ-UIF-2 | MUST | No page sets its own `DataContext`. Every page inherits the single `MainViewModel` instance that `MainWindow` creates. |
| REQ-UIF-3 | MUST | The page-name strings stay exactly as today. They are exposed as `const string`s plus `PageIds.All`, in today's order, in `Pages/PageIds.cs`. `MainWindow.PageNames` returns `PageIds.All`. `Navigate(string)` accepts the same strings. `MainWindow.PageFor(string)` returns the page instance. The smoke screenshot file names stay `{palette}-settings-{id.ToLowerInvariant()}.png` (absorbs REQ-UIF-23). |
| REQ-UIF-4 | MUST | An edit to any settings control on any page triggers exactly one debounced (400 ms) `AppHost.ApplySettings()` + `RefreshStatus()` + `RefreshProviderSections()`. Controls that are not settings opt out through an inheritable `AutoApply.Ignore` attached property, not by field identity. |
| REQ-UIF-5 | MUST | `Envoy.Core.Mvvm` provides `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand` (all `System.Windows.Input.ICommand`) and `ObservableObject`, covered by unit tests. `AsyncRelayCommand` requires a non-null `onError` (`ArgumentNullException` otherwise). While its task runs it reports `CanExecute == false` and `IsRunning == true` and ignores re-entrant `Execute` calls. Every exception from the task, including `OperationCanceledException`, goes to `onError` and is never rethrown. `MainViewModel` passes its single `ReportCommandError` handler to every `AsyncRelayCommand` (contract §8.8; absorbs REQ-UIF-6). |
| REQ-UIF-6 | — | Folded into REQ-UIF-5. |
| REQ-UIF-7 | MUST | Every `Button` and `Hyperlink` inside `Pages/*.xaml` runs through a `Command` binding to a `MainViewModel` command. Page XAML has no event-handler attributes. Page code-behind has no `RoutedEventArgs` handlers. |
| REQ-UIF-8 | MUST | Each command does the same thing as the handler it replaces, including flushing pending edits before "Translate" (test) and "Test" (voice). Closing `MainWindow` still applies a pending edit, releases the preview, sets `FirstRunCompleted = true` and saves (absorbs REQ-UIF-12). |
| REQ-UIF-9 | MUST | View models reach app-level actions only through `IShell`, which has exactly the 01 block of contract §3 and is implemented by `App`. Hotkey state reaches them through `AppHost.HotkeyResults` (contract §2.3). `App.RegisterHotkeys` replaces that list after every registration pass, nothing else writes it, and `App.HotkeyErrors` is deleted. Every modal box in `src/Envoy.App` goes through `Services/DialogService.cs`. `ViewModels/*.cs` contain no `App.Current`, `Process.Start`, `ProcessStartInfo` or `MessageBox`. |
| REQ-UIF-10 | MUST | The logic now in `MainWindow.xaml.cs` and `App.RegisterHotkeys` moves to `Envoy.Core` with unit tests and identical output: `PhrasePreview`, `OcrPackInstall`, `PlayerListText`, `TranslationTestText`, and `HotkeyText`. The user-facing strings these produce, plus the preview's `CapturePreviewText` and `PhrasebookText`, live in Core `*Text` classes as whole-sentence format strings (contract §8.7). |
| REQ-UIF-11 | MUST | Settings screenshots from `--screenshots` for the four pages captured today (dashboard, translation, capture, appearance), under each of today's five palettes, match the pre-change renders. The only allowed differences are the live capture-preview image and text, and sub-pixel anti-aliasing. |
| REQ-UIF-12 | — | Folded into REQ-UIF-8. |
| REQ-UIF-13 | MUST | `gen_stubs.py` emits the WPF base class for each known root element (`Window`, `UserControl`, `Application`, `Page`, `ResourceDictionary`), so a mismatch with the code-behind fails the typecheck. An unknown root is a hard error. Each stub starts with `// <auto-generated/>` so 08a's analyzers skip it. |
| REQ-UIF-14 | SHOULD | `gen_stubs.py` names stub files by full class name and fails when two XAML files declare the same `x:Class`. |
| REQ-UIF-15 | MUST | `gen_stubs.py` emits a compile-time reference (`nameof`) for every event-handler attribute in a XAML file, including ones inside templates. A handler name missing from the class makes `typecheck.sh` fail. |
| REQ-UIF-16 | SHOULD | `gen_stubs.py` honours `x:FieldModifier` and defaults to `internal`. |
| REQ-UIF-17 | SHOULD | `test_gen_stubs.py` and `test_check_ui_rules.py` (stdlib `unittest`, fixture XAML) cover REQ-UIF-14 and REQ-UIF-16 and the `check_ui_rules.py` rules. The REQ-UIF-13 and REQ-UIF-15 cases are required by those MUSTs' acceptance criteria. |
| REQ-UIF-18 | MUST | The smoke test has the contract §6 shape. It has three phases (setup, per palette, teardown). The per-palette loop runs over `ThemeManager.PaletteIds`, which this area adds as today's five theme ids; 07b appends high contrast later. For each palette and each `PageIds.All` entry, after `Navigate(id)` and a pump, `PageFor(id)` must be a loaded, visible instance of the expected `Pages` type with `ActualHeight > 0`, and every other page must be collapsed. With `--screenshots`, every page is saved (absorbs REQ-UIF-20), and teardown asserts `PaletteIds.Count × PageIds.All.Count` settings PNGs. Every section logs `[smoke] <section> ok elapsed_ms=<n>`. The test logs a warning when the total exceeds 420 s or one palette exceeds 30 s. `build.yml` waits `WaitForExit(600000)`. |
| REQ-UIF-19 | MUST | For each palette, a binding-error listener (`PresentationTraceSources.DataBindingSource`, `Error` level) is attached when the palette is applied and removed after the palette's last window, which covers `MainWindow`, overlay, compose, lookup and region selector. Each error is logged as `[smoke] binding error ({palette}): {message}` and fails the run. The only exception is an exact-message allowlist in `SmokeTest.cs`, documented in the PR. |
| REQ-UIF-20 | — | Folded into REQ-UIF-18. |
| REQ-UIF-21 | MUST | Preview state lives only in `CapturePreviewViewModel` (`IsActive`, `Image`, `Text`, `Stats`). `ChatWatcher.PublishFrames` is replaced by `IDisposable ChatWatcher.BeginPreview()`, which keeps an `Interlocked` count with idempotent dispose, and `IsPreviewing => count > 0`. The view model holds a lease exactly while `IsActive` is true and releases it on deactivate or `Dispose`. `ReadingChatPage` code-behind only toggles `IsActive`. Two view-model instances never interfere with each other (contract §2.8). |
| REQ-UIF-22 | — | Folded into REQ-UIF-1. |
| REQ-UIF-23 | — | Folded into REQ-UIF-3. |
| REQ-UIF-24 | — | Moved to the Definition of done (contract §10.4); see Design §7. |

## Design

### 1. Command toolkit: `src/Envoy.Core/Mvvm/`

`System.Windows.Input.ICommand` lives in the BCL (`System.ObjectModel`), so these types build on `net8.0`. They are
tested on Linux, and WPF binds to them unchanged. They do **not** use WPF's `CommandManager.RequerySuggested`,
because that type is WPF-only. Callers call `RaiseCanExecuteChanged()` explicitly.

```csharp
namespace Envoy.Core.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null);
    /// <returns>true if the value changed (and PropertyChanged was raised).</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter);          // canExecute?.Invoke() ?? true
    public void Execute(object? parameter);             // no-op if !CanExecute
    public void RaiseCanExecuteChanged();
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    // A parameter that is not T (and not null) => CanExecute false, Execute no-op (no InvalidCastException).
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter);
    public void Execute(object? parameter);
    public void RaiseCanExecuteChanged();
}

public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    /// <exception cref="ArgumentNullException">execute or onError is null.</exception>
    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null);
    public bool IsRunning { get; }                      // raises PropertyChanged(nameof(IsRunning))
    public bool CanExecute(object? parameter);          // !IsRunning && (canExecute?.Invoke() ?? true)
    public void Execute(object? parameter);             // fire-and-forget wrapper over ExecuteAsync
    public Task ExecuteAsync();                         // what tests await; never throws (every exception -> onError)
    public event EventHandler? CanExecuteChanged;       // raised when IsRunning flips and on RaiseCanExecuteChanged()
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseCanExecuteChanged();
}
```

**Threading.** `ExecuteAsync` awaits **without** `ConfigureAwait(false)`. When a WPF button calls it on the UI thread,
the `IsRunning`/`CanExecuteChanged` changes and `onError` all run back on the UI thread. Tests run with no
synchronisation context and get thread-pool continuations, which is fine for assertions. If `onError` itself throws,
the exception propagates from `ExecuteAsync`. That is a programming error and is not caught.

**Sync commands.** An exception from a `RelayCommand` propagates exactly as the old click handler's did, to the
dispatcher's unhandled-exception path. That path is `App.OnUnhandledException` today and 02's global handler later.

### 2. Logic and text moved to Core (behaviour-identical)

Every string below is moved verbatim from today's code. Format strings use positional placeholders, documented on each
constant, so they can move to `.resx` unchanged (contract §8.7). No wording changes.

```csharp
namespace Envoy.Core.Phrases;
public sealed record PhrasePreviewRow(string Icon, string Category, string Mine, string Theirs);
public static class PhrasePreview
{
    /// Mine = p.In(myLanguage) ?? p.In("en") ?? p.Id; Theirs = p.In(targetLanguage) ?? PhrasebookText.Missing.
    /// Order = book order.
    public static IReadOnlyList<PhrasePreviewRow> Build(PhraseBook book, string myLanguage, string targetLanguage);
    /// The exact JSON written today by OnEditPhrases when phrases.json doesn't exist (file content, not UI text).
    public const string UserFileTemplate = """ ...current literal... """;
}
public static class PhrasebookText
{
    public const string Missing = "—";
    public const string EditTitle = "Envoy";
    public const string EditInstructions =
        "Your phrases file is open. Save it, then press OK to reload.\n\nUse the same \"id\" as a built-in phrase to replace it.";
}

namespace Envoy.Core.Languages;
public static class OcrPackInstall
{
    public const string SettingsFallbackUri = "ms-settings:regionlanguage";
    /// "Language.OCR~~~{tag}~0.0.1.0", where tag = Code for zh-CN/zh-TW, else language.OcrTag.
    /// Throws ArgumentException if the tag contains anything other than ASCII letters, digits and '-'
    /// (the value is spliced into a PowerShell command run elevated).
    public static string CapabilityName(Language language);
    /// Looks the code up in LanguageCatalog (ArgumentException if unknown), then returns the exact argument string
    /// passed to powershell.exe today:
    /// -NoProfile -Command "Add-WindowsCapability -Online -Name '<cap>'; Write-Host 'Done - restart Envoy to use it.'; Start-Sleep 4"
    /// 08a's ShellLauncher.InstallOcrPack(languageTag) calls this with the same code (contract §5).
    public static string PowerShellArguments(string languageCode);
}

namespace Envoy.Core.Chat;
public static class PlayerListText
{
    /// Split on , ; \r \n, trim, drop empties, distinct (OrdinalIgnoreCase, first spelling wins).
    public static List<string> Parse(string? text);
    public static string Format(IEnumerable<string> players);   // string.Join(", ", players)
}

namespace Envoy.Core.Translation;
public static class TranslationTestText
{
    public const string SampleInput = "你好！再来一局吗？";
    public const string Pending = "…";
    /// Named placeholders (contract §8.7), filled by TextTemplate.Format.
    public const string ResultFormat = "{translation}    ({detected} → {mine}, via {provider})";
    /// Provider name = ProviderCatalog.Find(id)?.Name ?? id; language names via LanguageCatalog.NameOf.
    public static string Describe(TranslationResult result, string myLanguage);
    /// Today's text; 03 replaces it with error-kind text (see Risks).
    public const string FailureFormat = "⚠ {message}";
    public static string Failure(Exception ex);
}

namespace Envoy.Core.Text;
/// The one formatter for every Core *Text template (contract §8.7). Replaces each {name} or {name:format} with the
/// matching value formatted by culture (null = CurrentCulture); "{{" and "}}" are literal braces. An unknown or
/// missing name throws FormatException, so a template/argument mismatch fails its unit test. Output for the 01
/// templates is identical to today's string.Format output.
public static class TextTemplate
{
    public static string Format(string template, IFormatProvider? culture, params (string Name, object? Value)[] values);
}

namespace Envoy.Core.Ocr;
public static class CapturePreviewText
{
    public const string NoText = "(no text found)";
    /// Formatted with the current culture, as today.
    public const string StatsFormat = "{lines} lines · OCR {ocrMs:0} ms";
    public static string Stats(int lines, TimeSpan ocrTime);
    public static string Lines(IReadOnlyList<string> lines);    // NoText if empty, else string.Join(Environment.NewLine, lines)
}
```

**Hotkey results (contract §2.3).** These are new Core types, in namespace `Envoy.Core.Hotkeys`:

```csharp
public enum HotkeyStatus { Registered, NotSet, Taken, Invalid }   // 02 appends SkippedSafeMode (arrival rule)
public sealed record HotkeyRegistrationResult(string ActionId, string ActionName, string Combo, HotkeyStatus Status);

public static class HotkeyText
{
    /// Display names, keyed by ActionId (= the HotkeySettings property name): Compose → "Reply",
    /// ToggleOverlay → "Show/hide overlay", ToggleCapture → "Pause/resume", SelectRegion → "Select chat area",
    /// OverlayEditMode → "Move overlay", ScanAndTranslate → "Scan & translate".
    public static string ActionName(string actionId);
    /// {list} = comma-separated "Name (combo)" list.
    public const string TakenFormat = "These hotkeys are taken by another app: {list}";
    /// "" when no entry is Taken or Invalid; otherwise TakenFormat over those entries in list order.
    /// Invalid entries are included because today's text lists them too (no wording change in 01).
    public static string TakenSummary(IReadOnlyList<HotkeyRegistrationResult> results);
}
```

**Preview lease counter**, in namespace `Envoy.Core.Threading`. It backs `ChatWatcher.BeginPreview()` and is unit
tested in Core.

```csharp
public sealed class LeaseCounter
{
    public int Count { get; }                // Volatile read
    public bool IsHeld => Count > 0;
    public IDisposable Acquire();            // Interlocked.Increment; the lease's Dispose decrements once (idempotent)
}
```

`MainViewModel.MutedPlayersText` becomes `get => PlayerListText.Format(Settings.MutedPlayers); set =>
Settings.MutedPlayers = PlayerListText.Parse(value);`.

### 3. App-side infrastructure

**`src/Envoy.App/IShell.cs`** (new). This is exactly the 01 block of contract §3. 02, 06 and 07a each add their own
block in their own step.

```csharp
namespace Envoy.App;
/// <summary>App-level actions the view models may trigger. Implemented by App.</summary>
public interface IShell
{
    // 01
    void SelectRegion();
    void ToggleOverlayEditMode();
    void OpenCompose();
    void ScanAndTranslate();
    void ExitApp();
    void OpenUrl(string urlOrDataPath);          // 08a reroutes to ShellLauncher.TryOpen(target, Host.DataFolder)
    void InstallOcrPack(string languageTag);     // 08a reroutes to ShellLauncher.InstallOcrPack
    void ShowInfo(string title, string message); // → DialogService, owned by MainWindow when it is open
}
```

`App` becomes `public partial class App : Application, IShell`. `SelectRegion`, `ToggleOverlayEditMode`,
`OpenCompose`, `ScanAndTranslate` and `ExitApp` already exist with matching signatures. `SelectRegion` and
`ScanAndTranslate` stay `async void`. The new or changed members are:

- `public void OpenUrl(string urlOrDataPath)`. Today's static `App.OpenUrl` becomes this instance method. Its only
  callers were in `MainWindow.xaml.cs`, and they move to the view model. The body is unchanged:
  `Process.Start(new ProcessStartInfo(urlOrDataPath) { UseShellExecute = true })`.
- `public void InstallOcrPack(string languageTag)`. It runs `Process.Start(new ProcessStartInfo("powershell.exe",
  OcrPackInstall.PowerShellArguments(languageTag)) { UseShellExecute = true, Verb = "runas" })`. On `Win32Exception`
  (UAC declined) or `ArgumentException` (unknown code, unreachable from the built-in catalog), it calls
  `OpenUrl(OcrPackInstall.SettingsFallbackUri)`.
- `public void ShowInfo(string title, string message) => DialogService.ShowInfo(_main, title, message);`.
- `RegisterHotkeys` builds one `HotkeyRegistrationResult` per action, in today's order:
  - an empty or whitespace combo gives `NotSet`;
  - `!Hotkey.TryParse(combo, out _)` gives `Invalid`;
  - `!_hotkeys.Register(...)` gives `Taken`;
  - otherwise the result is `Registered`.

  `ActionId` is `nameof(HotkeySettings.X)` and `ActionName` is `HotkeyText.ActionName(id)`. The method ends with
  `Host.HotkeyResults = results;`. `HotkeyErrors` is deleted.

After this step, `Process.Start` appears only in `App.OpenUrl` and `App.InstallOcrPack`. Both are `IShell` members,
which 08a moves into `ShellLauncher` (contract §5). This area adds no P/Invoke and no network code.

**`src/Envoy.App/AppHost.cs`**. This is the only change to the file (contract §2.3):

```csharp
public IReadOnlyList<HotkeyRegistrationResult> HotkeyResults
{
    get => _hotkeyResults;
    set { _hotkeyResults = value; HotkeyResultsChanged?.Invoke(); }   // UI thread only; written only by App.RegisterHotkeys
}
public event Action? HotkeyResultsChanged;
private IReadOnlyList<HotkeyRegistrationResult> _hotkeyResults = [];
```

**`src/Envoy.App/Services/DialogService.cs`** (new; the single owner of modal boxes, per contract §2):

```csharp
internal static class DialogService
{
    /// MessageBox with OK + Information. Owner is used when non-null and loaded; otherwise ownerless.
    public static void ShowInfo(Window? owner, string title, string message);
    /// MessageBox with OK + Warning. Only for App's existing unhandled-exception box until 02 replaces it.
    public static void ShowWarning(Window? owner, string title, string message);
    // 02 adds Confirm(...). No placeholder members here.
}
```

All three existing `MessageBox.Show` sites route through it, with identical text, caption, buttons and icon:

- Edit phrases goes through `IShell.ShowInfo`, owned by `MainWindow`.
- `ComposeWindow`'s send failure calls `ShowInfo(null, "Envoy", …)`. 02 replaces it with a problem toast.
- `App.OnUnhandledException` calls `ShowWarning(null, "Envoy", …)`. 02 replaces it with its global handler.

Contract §2.2 forbids modal surfaces other than user-started `DialogService` confirmations. The two non-user-started
boxes are today's behaviour, and 02 removes them.

**`src/Envoy.App/Services/ChatWatcher.cs`**. `PublishFrames` is removed and replaced with:

```csharp
private readonly LeaseCounter _preview = new();
/// Frames are published to FrameCaptured while at least one lease is held. Each call also Invalidate()s,
/// as turning the old flag on did.
public IDisposable BeginPreview() { var lease = _preview.Acquire(); Invalidate(); return lease; }
public bool IsPreviewing => _preview.IsHeld;
```

The two existing reads of `PublishFrames` become `IsPreviewing`: the "skip when not reading" check and the
unchanged-fingerprint early return. The `FrameCaptured` raise is guarded by `IsPreviewing`. 05's rewrite keeps this
API. Its tick step 6 and step 11 key off `IsPreviewing` (contract §2.7, §2.8).

**`src/Envoy.App/Services/ThemeManager.cs`**. Add
`public static IReadOnlyList<string> PaletteIds { get; } = [.. Themes.Select(t => t.Id)];`. 07b redefines it to append
high contrast. Nothing else changes.

**`src/Envoy.App/Controls/AutoApply.cs`** (new)

```csharp
public static class AutoApply
{
    // Inherits: set it on a container to exclude everything inside it.
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(AutoApply), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIgnore(DependencyObject d);
    public static void SetIgnore(DependencyObject d, bool value);
}
```

These elements set it to `True`:

- the sidebar `Nav` StackPanel, which replaces the `GroupName: "Nav"` check;
- the `TranslationPage` test input;
- the `ReadingChatPage` preview text box;
- the `PhrasebookPage` preview-language ComboBox.

`MainWindow.OnEdited` becomes
`if (e.OriginalSource is DependencyObject d && AutoApply.GetIgnore(d)) return; if (!IsLoaded) return; _applier.Schedule();`.

**`src/Envoy.App/Services/SettingsAutoApplier.cs`** (new; wraps the existing `DispatcherTimer` logic)

```csharp
internal sealed class SettingsAutoApplier
{
    public SettingsAutoApplier(Dispatcher dispatcher, TimeSpan delay, Action apply); // delay = 400 ms (unchanged)
    public bool IsPending { get; }
    public void Schedule();        // (re)start the timer
    public void ApplyNow();        // stop the timer and apply unconditionally (before Test translate / Test voice)
    public void FlushIfPending();  // apply only if a change is pending (window close)
}
```

The `apply` action is the old `ApplyNow` body:
`host.ApplySettings(); vm.RefreshStatus(); vm.RefreshProviderSections();`. `MainWindow` creates the applier and
passes it to `MainViewModel`.

**`MainViewModel`** keeps its public surface and gains a constructor, commands and new state.

- **Constructor.** It is `MainViewModel(AppHost host, IShell shell, SettingsAutoApplier applier)`. `MainWindow` gains
  `MainWindow(AppHost host, IShell shell)`. The existing `MainWindow(AppHost host) : this(host, App.Current)` stays,
  so the call sites in `App.ShowMain` and the smoke test don't change. 02's safe-mode check builds a second
  `MainWindow` the same way.
- **Base class.** It derives from `ObservableObject` and implements `IDisposable`.
  `public void Notify([CallerMemberName] string? name = null)` stays as a wrapper over `OnPropertyChanged`.
- **`HotkeyErrors`.** It becomes `HotkeyText.TakenSummary(Host.HotkeyResults)`. The constructor subscribes
  `Host.HotkeyResultsChanged += OnHotkeyResultsChanged`, which calls `Notify(nameof(HotkeyErrors))`. `RefreshStatus`
  still notifies it too.
- **`ReportCommandError(Exception ex)`.** This is the one handler passed to every `AsyncRelayCommand` (contract §8.8).
  In 01 it calls `Trace.TraceError("command failed: {0}", ex)`. Every async command body in 01 catches its own
  exceptions, so the handler is unreachable in normal use. 02 replaces the body with a log at Error plus a problem
  toast.
- **New commands.** All are get-only properties created in the constructor:

| Command | Type | Replaces | Body |
| --- | --- | --- | --- |
| `SelectRegionCommand` | `RelayCommand` | `OnSelectRegion` (War Room, Reading Chat) | `_shell.SelectRegion()` |
| `ToggleCaptureCommand` | `RelayCommand` | `OnToggleCapture` | `Host.ToggleCapture()` |
| `ShowSampleMessagesCommand` | `RelayCommand` | `OnDemo` (War Room, Overlay) | `Host.ShowDemoMessages()` |
| `MoveOverlayCommand` | `RelayCommand` | `OnEditOverlay` (War Room, Overlay) | `_shell.ToggleOverlayEditMode()` |
| `OpenReplyBoxCommand` | `RelayCommand` | `OnCompose` | `_shell.OpenCompose()` |
| `ScanAndTranslateCommand` | `RelayCommand` | `OnScan` | `_shell.ScanAndTranslate()` |
| `NewMatchCommand` | `RelayCommand` | `OnNewMatch` | `Host.NewMatch()` |
| `DownloadUpdateCommand` | `RelayCommand` | `OnDownloadUpdate` | `if (Host.Update is { } u) _shell.OpenUrl(u.PageUrl)`. `CanExecute` stays true, because the banner is only visible when an update exists. 06 replaces this command. |
| `TestTranslateCommand` | `AsyncRelayCommand` | `OnTestTranslate` | `_applier.ApplyNow(); TranslationTestOutput = Pending; try { … Describe(…) } catch (Exception ex) { TranslationTestOutput = Failure(ex); }`, with `onError: ReportCommandError` |
| `InstallOcrCommand` | `RelayCommand<string>` | `OnInstallOcr` (parameter = language code) | `_shell.InstallOcrPack(code)` |
| `TestVoiceCommand` | `RelayCommand` | `OnTestVoice` | `_applier.ApplyNow(); Host.TestVoice();` |
| `EditPhrasesCommand` | `RelayCommand` | `OnEditPhrases` | Write `PhrasePreview.UserFileTemplate` if missing → `_shell.OpenUrl(path)` → `_shell.ShowInfo(PhrasebookText.EditTitle, PhrasebookText.EditInstructions)` → `Host.ReloadPhrases()` → `RefreshPhrasePreview()` |
| `OpenLogsCommand` / `OpenDataFolderCommand` | `RelayCommand` | `OnOpenLogs` / `OnOpenData` | `Directory.CreateDirectory(folder); _shell.OpenUrl(folder)`. Both folders sit under the data folder, so 08a's `TryOpen` allows them. |
| `OpenLinkCommand` | `RelayCommand<string>` | `OnLink` | `_shell.OpenUrl(url)`. The Hyperlink keeps `NavigateUri` for the tooltip and accessibility, and binds `CommandParameter` to the same literal. |
| `QuitCommand` | `RelayCommand` | `OnQuit` | `_shell.ExitApp()` |

- **New state:**
  - `string TranslationTestInput`, which defaults to `TranslationTestText.SampleInput`;
  - `string TranslationTestOutput`, which defaults to `""`;
  - `Language? PhrasePreviewLanguage`, which defaults to
    `LanguageCatalog.Find(Host.Pipeline.LastOpponentLanguage) ?? LanguageCatalog.Find("zh-CN")`. Its setter calls
    `RefreshPhrasePreview()`;
  - `IReadOnlyList<PhrasePreviewRow> PhraseRows` and `void RefreshPhrasePreview()`;
  - `CapturePreviewViewModel CapturePreview`.
- **Disposal.** `Dispose()` unsubscribes `HotkeyResultsChanged` and disposes `CapturePreview`. `MainWindow.OnClosed`
  calls it.

All command bodies run on the UI thread, as the click handlers did. No command adds a new failure mode.

**`src/Envoy.App/ViewModels/CapturePreviewViewModel.cs`** (new). It replaces `SetPreview`/`OnFrame` and is the only
home for preview state (contract §2.8). 04 adds "Understood as" and the copy-as-test-capture command here, and 05 adds
`MetricsText`/`HealthText` with their 1 s timer. 07a's guide creates its own instance.

```csharp
public sealed class CapturePreviewViewModel : ObservableObject, IDisposable
{
    public CapturePreviewViewModel(AppHost host, Dispatcher dispatcher); // subscribes host.Watcher.FrameCaptured
    /// true: _lease ??= host.Watcher.BeginPreview().  false: _lease?.Dispose(); _lease = null.
    public bool IsActive { get; set; }
    public ImageSource? Image { get; }   // frozen BitmapSource built on the UI thread (same code as OnFrame)
    public string Text { get; }          // CapturePreviewText.Lines(frame.Lines)
    public string Stats { get; }         // CapturePreviewText.Stats(frame.Lines.Count, frame.OcrTime)
    public void Dispose();               // IsActive = false; unsubscribe FrameCaptured (idempotent)
}
```

`OnFrame` marshals to the UI thread with `BeginInvoke`, as today. There it returns early unless `IsActive` is true,
which replaces the old `if (!PublishFrames) return;`.

`ReadingChatPage.xaml.cs` holds the page's only code-behind logic:

```csharp
IsVisibleChanged += (_, _) => { if (DataContext is MainViewModel vm) vm.CapturePreview.IsActive = IsVisible; };
```

Visibility follows `NavCapture.IsChecked`, so the preview starts and stops at the same moments as before. There is
one intended internal difference: frames also stop while the main window is hidden to the tray. Nothing is drawn in
that state, so the user sees no change.

**`src/Envoy.App/Pages/`** (new). Each page is a `*.xaml` + `*.xaml.cs` pair. The code-behind is just
`public partial class XPage : UserControl { public XPage() => InitializeComponent(); }`, except for `ReadingChatPage`
as described above.

- **Root content.** The page root is the **exact** `ScrollViewer Padding="32,24"` subtree cut from `MainWindow.xaml`.
  The `Visibility` binding moves out to the host element. `MaxWidth` stays 900, or 760 for About.
- **Namespaces and resources.** Each page XAML declares only the namespaces it uses: `x`, the default namespace, and
  `c` for `Controls` where `HotkeyBox`, `SecretBox` or `AutoApply` appear. `StaticResource` and `DynamicResource` keys
  resolve from the `App.xaml` merged dictionaries, as before.
- **Accessibility.** No interactive control is added. Moved controls keep their existing attributes. The
  `AutomationProperties.Name` rule (contract §8.8) applies to *new* controls from this step on.
- **Bindings.**
  - The `InstallOcr` button in the OCR `DataTemplate` binds
    `Command="{Binding DataContext.InstallOcrCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
    CommandParameter="{Binding Code}"`.
  - The phrase list binds `ItemsSource="{Binding PhraseRows}"`, and the preview `ComboBox` binds
    `SelectedItem="{Binding PhrasePreviewLanguage}"`.
  - `TestInput` binds to `TranslationTestInput` with `UpdateSourceTrigger=PropertyChanged`, and `TestOutput` binds
    to `TranslationTestOutput`.
  - The preview controls bind to `CapturePreview.Image`, `CapturePreview.Text` and `CapturePreview.Stats`.

  Pages need no `x:Name` fields, but they are allowed.

**`src/Envoy.App/Pages/PageIds.cs`** (new)

```csharp
public static class PageIds
{
    public const string Dashboard = "Dashboard", Translation = "Translation", Capture = "Capture", Overlay = "Overlay",
        Reply = "Reply", Phrases = "Phrases", Hotkeys = "Hotkeys", Appearance = "Appearance", About = "About";
    public static IReadOnlyList<string> All { get; } = [Dashboard, Translation, Capture, Overlay, Reply, Phrases, Hotkeys, Appearance, About];
}
```

**`MainWindow.xaml`** after the split (about 90 lines):

- The title bar and sidebar are unchanged, except that `Nav` gains `c:AutoApply.Ignore="True"`.
- The page grid holds nine hosts, each like
  `<p:WarRoomPage x:Name="WarRoomPage" Visibility="{Binding IsChecked, ElementName=NavDashboard, Converter={StaticResource BoolToVisibility}}" />`.
  These `ElementName` bindings stay inside `MainWindow`'s namescope, so they still resolve.
- 02 places the safe-mode banner above the page hosts (contract §8.6).

**`MainWindow.xaml.cs`** after the split (about 80 lines) keeps:

- the constructors, which create the view model and the applier, start the status timer and register the bubbling
  edit handlers;
- `PageNames => PageIds.All`;
- `Navigate`;
- `public UserControl PageFor(string pageId)`, a switch to the named host. The smoke test uses it, and so does 02
  (`((AboutPage)PageFor(PageIds.About)).TroubleshootingFrame.BringIntoView()`);
- `OnEdited`;
- the window-chrome handlers `OnMinimize`, `OnMaximize` and `OnClose`, which stay as view handlers;
- `OnClosed`, which runs
  `_statusTimer.Stop(); _applier.FlushIfPending(); _vm.Dispose(); FirstRunCompleted = true; SaveNow();`.

**`MainViewModel` file split (part of REQ-UIF-1).** `ViewModels/MainViewModel.cs` keeps the shared part: the
constructor, `Host`, `Settings`, `RefreshStatus`, `Notify`, `ReportCommandError` and `Dispose`. The page sections go
in `MainViewModel.Translation.cs`, `MainViewModel.ReadingChat.cs`, `MainViewModel.Phrasebook.cs` and
`MainViewModel.Actions.cs` (the commands). The helper types go in `ViewModels/Option.cs`, `OcrLanguageItem.cs` and
`ThemeOption.cs`. Later areas add their own partials, such as `MainViewModel.Diagnostics.cs` (02). Moving code
changes no member signature.

### 4. Typecheck tools (`tools/typecheck/`)

**`gen_stubs.py`**

- **Base class (REQ-UIF-13).** A map from root element to base type:
  `{Window: System.Windows.Window, UserControl: System.Windows.Controls.UserControl, Application: System.Windows.Application, Page: System.Windows.Controls.Page, ResourceDictionary: System.Windows.ResourceDictionary}`.
  The stub becomes `// <auto-generated/>` followed by `public partial class {name} : {base}`, which matches what the
  WPF markup compiler emits. An unmapped root prints `"{path}: unsupported x:Class root <{tag}>"` and exits 1.
- **Event handlers (REQ-UIF-15).**
  - An attribute matches when it has no namespace, its local name (or the part after the last `.`, for attached
    events such as `ButtonBase.Click`) is in a fixed `EVENT_NAMES` set, and its value matches
    `^[A-Za-z_][A-Za-z0-9_]*$`.
  - `EVENT_NAMES` has about 50 entries: Click, Checked, Unchecked, Loaded, Unloaded, SelectionChanged, TextChanged,
    ValueChanged, PasswordChanged, RequestNavigate, Mouse*/PreviewMouse*, Key*/PreviewKey*, Got/LostFocus,
    Got/LostKeyboardFocus, SizeChanged, IsVisibleChanged, Closing, Closed, Activated, Deactivated, ContentRendered,
    SourceInitialized, StateChanged, LocationChanged, DragStarted/DragDelta/DragCompleted, ScrollChanged,
    Expanded/Collapsed, Opened, Drop/DragOver/DragEnter/DragLeave.
  - Each match adds a line `_ = nameof({value});` inside a `private void __XamlEventHandlers() { … }` method. Template
    handlers are included, because the real markup compiler wires them to the root class too. A missing handler then
    fails with CS0103.
  - `EVENT_NAMES` and the matcher are exposed as functions so that `check_ui_rules.py` can reuse them.
- **Stub file names (REQ-UIF-14, SHOULD).** Files are named `{namespace}.{name}.g.cs`. A second XAML file with an
  `x:Class` already seen makes the script exit 1, naming both paths.
- **Field modifiers (REQ-UIF-16, SHOULD).** `x:FieldModifier` (case-insensitive
  `public|internal|private|protected|protected internal`) sets the field's modifier. Any other value is an error.

**`check_ui_rules.py`** (new, stdlib only). It turns the structural rules into a mechanical check. Each violation
prints `path:line: UIF-Rn message`, and any violation exits 1. 08a may later move R4 and R5 into
`BannedSymbols.txt`/`policy.json`. Until then this script is the check.

| Rule | Check | Backs |
| --- | --- | --- |
| R1 | `Pages/*.xaml` has no event-handler attribute (the `gen_stubs` matcher) | REQ-UIF-7 |
| R2 | `Pages/*.xaml.cs` contains no `RoutedEventArgs` and no `DataContext =` | REQ-UIF-7, REQ-UIF-2 |
| R3 | `Pages/*.xaml` has no `DataContext=` attribute (`DataContext.` inside a binding path is fine) | REQ-UIF-2 |
| R4 | `ViewModels/*.cs` contains no `App.Current`, `Process.Start`, `ProcessStartInfo` or `MessageBox` | REQ-UIF-9 |
| R5 | `MessageBox.Show` in `src/Envoy.App/**/*.cs` appears only in `Services/DialogService.cs` | REQ-UIF-9 |
| R6 | `Windows/MainWindow.xaml` contains no `ScrollViewer` | REQ-UIF-1 |
| R7 | These files exist: the nine `Pages/*Page.xaml(.cs)` pairs, `Pages/PageIds.cs`, the four `MainViewModel.*.cs` partials, `ViewModels/CapturePreviewViewModel.cs` and `Services/DialogService.cs` | REQ-UIF-1, REQ-UIF-21 |

**`typecheck.sh`** runs these steps in order:

1. `python3 -m unittest discover -s "$here" -p 'test_*.py'`;
2. `gen_stubs.py`;
3. `check_resources.py`;
4. `check_ui_rules.py`;
5. `dotnet build`.

**Unchanged.** `check_resources.py` already globs `**/*.xaml`, so it covers `Pages/` without changes. The
`TypeCheck.csproj` compile glob already includes `src/Envoy.App/**/*.cs`.

### 5. Smoke test (`src/Envoy.App/SmokeTest.cs`, contract §6)

The `Run(App app, string[] args)` signature is unchanged; 02 changes it. This area sets up the three-phase shape that
later areas slot into. A helper `Section(string name, Stopwatch sw)` writes `[smoke] {name} ok elapsed_ms={n}`. Until
02 brings `LogHub`, warnings are written as `[smoke] WARNING …` lines.

1. **Setup (once).**
   - Create the temp root `%TEMP%\envoy-smoke-{guid}` and the host on `{root}\settings.json`. 02 moves this to
     `{root}\data` with `DataFolder`.
   - List the OCR languages.
   - Run the compose **provider warm-up**: one `host.Translator.TranslateAsync("good game! one more?", …)` with a
     10 s timeout. A failure logs a `WARNING` but does not fail the run, as today.
   - Run the existing one-shot capture + OCR check, moved here from the end as section `capture-ocr`.
   - Assert that `PageIds.All` equals the legacy nine-name list, in order (section `page-ids`).
2. **Per palette.** `foreach (var id in ThemeManager.PaletteIds)`:
   1. Set `host.Settings.Theme = id`, call `ThemeManager.Apply(id)`, and start the binding-error listener.
   2. **Pages.** Open `MainWindow`. For each `PageIds.All` entry: `Navigate`, pump, and assert that `PageFor(page)`
      is the expected `Pages` type with `IsLoaded`, `IsVisible` and `ActualHeight > 0`, and that every other page is
      `Collapsed`. With `--screenshots`, call `host.ShowDemoMessages()` once and then save
      `{id}-settings-{page.ToLowerInvariant()}.png`. Section: `pages({id})`.
   3. **MainWindow-dependent checks.** 01's own checks come first; later areas append theirs in contract order.
      - `commands({id})`, first palette only. Every `Button` (not `ToggleButton`) in each page's visual tree and
        every `Hyperlink` in its logical tree has a non-null `Command`. Each page is navigated first, so
        `ItemsControl` templates are realised.
      - `preview-lease({id})`, first palette only. `Navigate(Capture)` → `host.Watcher.IsPreviewing` is true. A
        second `new CapturePreviewViewModel(host, Dispatcher)` with `IsActive = true` and then `false` leaves
        `IsPreviewing` true, which is the guide-versus-main case. `Navigate(Dashboard)` → false. The second instance
        is disposed.
      - `autoapply({id})`, first palette only. Count `host.SettingsApplied`, then:
        1. Find the `CheckBox` on `PageFor(Overlay)` whose `IsChecked` binding path is
           `Settings.Overlay.ShowProvider`. Toggle it and wait 600 ms. The count must rise by exactly 1.
        2. Find the `TextBox` on `PageFor(Translation)` where `AutoApply.GetIgnore` is true. Set its text and wait
           600 ms. The count must not change.
        3. Toggle the CheckBox back.
   4. **Close.** On the first palette only, toggle `ShowProvider` once more and immediately call `main.Close()`.
      Then reload `settings.json` with `SettingsStore` and assert that `Overlay.ShowProvider` has the new value and
      `FirstRunCompleted` is true (section `close-flush`). That proves the pending-edit flush on close. Other
      palettes just call `main.Close()`. Closing must not throw, and afterwards `IsPreviewing` must be false.
   5. **Other windows**, as today: overlay (show, demo, edit mode on and off), compose, lookup, region selector.
      Compose no longer sleeps a fixed 2.5 s. With `--screenshots` it polls up to 2.5 s until `compose.Preview.Text`
      is neither `""` nor `"…"`, and the setup warm-up usually makes that instant.
   6. Stop the listener and fail on any collected binding error that is not on the exact-message allowlist. Log
      `[smoke] palette {id} ok elapsed_ms={n}`, plus a `WARNING` when that exceeds 30 s.
3. **Teardown.** Dispose the host. With `--screenshots`, assert that the count of `*-settings-*.png` equals
   `PaletteIds.Count × PageIds.All.Count`. Delete the temp root, ignoring an `IOException` with a `WARNING`. Log the
   total elapsed time, plus a `WARNING` when it exceeds 420 s.

**Binding errors.** At setup, call `PresentationTraceSources.Refresh()` and set
`PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error`. A private nested
`BindingErrorListener : TraceListener` collects `WriteLine` messages. Each collected message is logged as
`[smoke] binding error ({palette}): {message}`, and the run then fails with `InvalidOperationException`.

**`build.yml`.** The only change in this step is `$p.WaitForExit(180000)` → `$p.WaitForExit(600000)` (contract §6).

### 6. Startup, settings and allowlists

- **Startup and exit.** No reordering. `OnStartup`, `ExitApp` and the tray are untouched, apart from
  `RegisterHotkeys` writing `Host.HotkeyResults`. 02 writes the contract §4 skeleton around them.
- **Settings.** None added. `AppSettings.cs` and the JSON are unchanged, so no `[Personal]` or `[Location]` markers
  are needed.
- **P/Invoke allowlist additions.** None. `Native.cs` is unchanged.
- **Network allowlist additions.** None. No new HTTP code or hosts.
- **Process launching.** No new launch sites. The elevated PowerShell launch moves from `MainWindow.xaml.cs` into
  `App.InstallOcrPack`, next to the existing `App.OpenUrl`. 08a moves both into `ShellLauncher` (contract §5).

### 7. Docs and definition of done (contract §10; formerly REQ-UIF-24)

- **`CLAUDE.md`.** Make these edits:
  - Rewrite the "Settings changes" rule as "add the property to `AppSettings`, bind it on the relevant page in
    `src/Envoy.App/Pages/` (edits auto-apply; a non-setting input sets `c:AutoApply.Ignore="True"`) …" (contract §7).
  - Add a rule that every new interactive control gets `AutomationProperties.Name` (contract §8.8).
  - Add a rule that user-facing text built in code lives in Core `*Text` classes (contract §8.7).
  - Amend the "New windows … SmokeTest.cs" rule to say "in the per-palette phase; new pages are added to
    `PageIds.All`".
- **`docs/architecture.md`.** List `Pages/`, `Envoy.Core.Mvvm`, `IShell`, `DialogService`, the preview lease and
  `HotkeyResults`.
- **`CHANGELOG.md`.** Add an "Internal" line under `## Unreleased`.
- **This spec.** Set `Status: Implemented` and add a `## Deviations` section.

### Failure behaviour

- An exception in a sync command propagates exactly as the old click handler's did.
- An exception in `TestTranslateCommand` is caught in its body and shown as `⚠ message`, as today. The command also
  ignores double-clicks while a test translation is running. That is a small, invisible improvement: the button greys
  out through the existing `IsEnabled=false` style.
- If the elevation prompt for "Install" is declined, Windows language settings open, as today. An unknown code also
  opens settings. The old code returned silently in that case, but the path is unreachable from the built-in catalog.

## Acceptance criteria

Each MUST is verified by at least one of the contract §9 kinds: a Core unit test, `typecheck.sh`, a named smoke
section, a CI check, or a numbered manual step (M-steps feed 08b's `docs/releasing.md`; P-steps run once on this PR).

| Id | Maps to | Verified by |
| --- | --- | --- |
| AC-UIF-1 | REQ-UIF-1, REQ-UIF-2 | `typecheck.sh` passes, including `check_ui_rules.py` R2, R3, R6 and R7. Smoke `pages({id})` passes for every palette. |
| AC-UIF-2 | REQ-UIF-3 | Smoke `page-ids` passes. With `--screenshots`, the teardown count assertion passes and the CI artifact keeps the four legacy names `{palette}-settings-{dashboard,translation,capture,appearance}.png`. |
| AC-UIF-3 | REQ-UIF-4 | Smoke `autoapply(...)` passes: one apply per settings edit, none for an ignored control. Manual step **M2**. |
| AC-UIF-4 | REQ-UIF-5 | `MvvmTests.cs` passes under `dotnet test`. |
| AC-UIF-5 | REQ-UIF-7 | `check_ui_rules.py` R1 and R2 pass in `typecheck.sh`. Smoke `commands(...)` passes. |
| AC-UIF-6 | REQ-UIF-8 | Smoke `close-flush` passes. Manual step **M1**. |
| AC-UIF-7 | REQ-UIF-9 | `check_ui_rules.py` R4 and R5 pass. `typecheck.sh` compiles `App : IShell` against the 01 block, and `App.HotkeyErrors` no longer exists. `HotkeyTextTests` passes. In M1, the Edit-phrases box is owned by the main window. |
| AC-UIF-8 | REQ-UIF-10 | `TextTemplateTests`, `PhrasePreviewTests`, `OcrPackInstallTests`, `PlayerListTextTests`, `TranslationTestTextTests`, `CapturePreviewTextTests` and `HotkeyTextTests` pass. |
| AC-UIF-9 | REQ-UIF-11 | One-off step **P1** on this PR, with the result recorded in the PR description. |
| AC-UIF-10 | REQ-UIF-13, REQ-UIF-15 | `test_gen_stubs.py` cases `test_usercontrol_base_class`, `test_unsupported_root_fails`, `test_auto_generated_header`, `test_event_handlers_emitted_including_templates`, `test_attached_event_name` and `test_binding_values_not_treated_as_handlers` pass in `typecheck.sh` (CI `check` job). |
| AC-UIF-11 | REQ-UIF-14, REQ-UIF-16, REQ-UIF-17 (SHOULD) | The remaining `test_gen_stubs.py` cases and `test_check_ui_rules.py` pass. |
| AC-UIF-12 | REQ-UIF-18 | The Windows CI smoke test passes within `WaitForExit(600000)`. `smoke.log` has `[smoke] palette {id} ok` for every `ThemeManager.PaletteIds` entry, a `pages({id})` section for each, and `elapsed_ms` on every section line. |
| AC-UIF-13 | REQ-UIF-19 | The smoke test passes with no `binding error` lines outside the allowlist. One-off step **P2** on this PR. |
| AC-UIF-14 | REQ-UIF-21 | `LeaseCounterTests` passes. Smoke `preview-lease(...)` passes, and `IsPreviewing` is false after every `main.Close()`. `check_ui_rules.py` R2 and R7 pass. |

**Manual steps.** M-steps are added to 08b's pre-release pass. P-steps run once on this PR.

- **M1 — settings buttons (Windows).** Click every page button once and compare the result with the previous
  release:
  - **War Room:** Select chat area, Pause/resume, Show sample messages, Move the overlay, Open reply box, Scan &
    translate, New match.
  - **Translation:** "Translate" shows `…` and then the result, and the two "Get a key" links open the browser.
  - **Reading Chat:** Select chat area, and Install. Declining UAC opens Language settings.
  - **Overlay:** Drag to place, Show sample messages, and Test voice, which uses the just-edited voice settings.
  - **Phrasebook:** Edit my phrases opens the file, shows an info box owned by the main window, and reloads the
    list.
  - **Appearance:** Open logs.
  - **About:** Open settings folder, Quit Envoy.
- **M2 — settings round trip (Windows).**
  1. Change "Main provider" on Translation and a hotkey on Hotkeys. Wait 1 s, then quit from the tray. Both values
     must be in `settings.json`.
  2. Type in the Translation test box, switch pages, and change the phrase preview language. The settings file's
     modified time must not change.
  3. Reopen Envoy. There must be no first-run behaviour.
- **M3 — over the game (any AoE title, borderless).** With Envoy reading chat, open settings over the game, switch
  pages including Reading Chat, then close the window. Capture keeps running and the overlay is unaffected.
- **P1 — screenshot parity.**
  1. Download the `screenshots` artifact from the last `main` run before the change, or use the `ci-screenshots`
     branch, and the artifact from this PR's run.
  2. Compare the 20 legacy settings PNGs (5 palettes × 4 pages): dimensions must be identical, with no visible
     difference outside the capture-preview box. Flip between the images, or use `compare` from ImageMagick.
- **P2 — listener sanity.** Temporarily add a bad binding to one page and push. The smoke test must fail with a
  `binding error` line. Revert the change and record the run link in the PR.

## Test plan

### Unit tests (`tests/Envoy.Core.Tests`, xUnit)

**`MvvmTests.cs`**

- `RelayCommand_Execute_InvokesAction`: the action runs once per `Execute`.
- `RelayCommand_CanExecuteFalse_ExecuteIsNoOp`.
- `RelayCommand_RaiseCanExecuteChanged_RaisesEvent`: the event is raised exactly once, with `sender == command`.
- `RelayCommandT_PassesTypedParameter`: `Execute("ko")` delivers `"ko"`.
- `RelayCommandT_WrongParameterType_CanExecuteFalseAndNoThrow`: `Execute(42)` on `RelayCommand<string>` neither throws
  nor invokes the action.
- `AsyncRelayCommand_NullOnError_Throws`: the constructor throws `ArgumentNullException`.
- `AsyncRelayCommand_WhileRunning_CanExecuteFalseAndIsRunningTrue`: uses a `TaskCompletionSource` to hold the task.
- `AsyncRelayCommand_ReentrantExecute_Ignored`: the call counter stays 1.
- `AsyncRelayCommand_RaisesCanExecuteChanged_OnStartAndFinish`: the event fires at least twice.
- `AsyncRelayCommand_Exception_RoutedToOnError_NotThrown`: `await ExecuteAsync()` completes and `onError` receives the
  exception. Afterwards `IsRunning` is false and `CanExecute` is true.
- `AsyncRelayCommand_Cancellation_RoutedToOnError`: an `OperationCanceledException` reaches `onError`.
- `ObservableObject_SetProperty_RaisesOnlyOnChange`.

**`LeaseCounterTests.cs`**

- `Acquire_SetsHeld_DisposeClears`.
- `TwoLeases_DisposeOne_StillHeld`: this is the main-plus-guide case.
- `Dispose_Twice_DecrementsOnce`.
- `Parallel_AcquireRelease_EndsAtZero`: 8 threads run 10,000 acquire/dispose pairs each, and `Count` ends at 0.

**`HotkeyTextTests.cs`**

- `TakenSummary_AllRegisteredOrNotSet_Empty`.
- `TakenSummary_MatchesLegacyText`: Reply `Ctrl+Shift+T` Taken gives
  `"These hotkeys are taken by another app: Reply (Ctrl+Shift+T)"`.
- `TakenSummary_IncludesInvalid_InListOrder`.
- `ActionName_CoversEveryHotkeySettingsProperty`: reflects over `HotkeySettings` string properties.

**`PhrasePreviewTests.cs`**

- `Build_UsesMyLanguageThenEnglishThenId`.
- `Build_MissingTargetShowsDash`.
- `Build_PreservesBookOrder`.
- `UserFileTemplate_LoadsAsUserPhrase`: id `my-first-phrase`, with `ko` text `만나서 반가워요!`.

**`OcrPackInstallTests.cs`**

- `CapabilityName_ChineseUsesCode`: covers `zh-CN` and `zh-TW`.
- `CapabilityName_OthersUseCatalogOcrTag`: asserted against the catalog, not hard-coded tags.
- `CapabilityName_RejectsUnsafeTag`: `OcrTag = "en'; rm"` throws `ArgumentException`.
- `PowerShellArguments_MatchesLegacyString`: exact equality for `ko`.
- `PowerShellArguments_UnknownCode_Throws`.

**`PlayerListTextTests.cs`**

- `Parse_SplitsOnCommaSemicolonNewlines`.
- `Parse_TrimsAndDropsEmpty`.
- `Parse_DistinctIgnoreCase_FirstSpellingWins`.
- `Parse_Null_ReturnsEmpty`.
- `Format_JoinsWithCommaSpace`.
- `RoundTrip_FormatThenParse_Identity`.

**`TranslationTestTextTests.cs`**

- `Describe_KnownProvider_UsesDisplayName`: builds the expected value from `LanguageCatalog.NameOf` and
  `ProviderCatalog`.
- `Describe_UnknownProvider_FallsBackToId`.
- `Describe_NullDetected_UsesNameOfNull`.
- `Failure_PrefixesWarning`.

**`CapturePreviewTextTests.cs`**

- `Lines_Empty_NoText`.
- `Lines_JoinsWithNewLine`.
- `Stats_MatchesLegacyFormat`: `3` and `41.6 ms` give `"3 lines · OCR 42 ms"` under the invariant culture.

### Typecheck-tool tests (stdlib `unittest`, run first by `typecheck.sh`)

Fixtures live in `tools/typecheck/fixtures/`:

- `SampleControl.xaml`: a UserControl with a named element, an `x:FieldModifier="public"` element, and a `Click` inside
  a `DataTemplate`;
- `Dup1.xaml` and `Dup2.xaml`, which declare the same `x:Class`;
- `Weird.xaml`, which has a `<Canvas x:Class>` root;
- a mini `ui_rules/` tree with one violation per rule.

**`test_gen_stubs.py`**

- **MUST** (REQ-UIF-13 and REQ-UIF-15):
  - `test_usercontrol_base_class`;
  - `test_unsupported_root_fails`;
  - `test_auto_generated_header`;
  - `test_event_handlers_emitted_including_templates`;
  - `test_attached_event_name`;
  - `test_binding_values_not_treated_as_handlers`.
- **SHOULD:**
  - `test_file_named_by_full_class`;
  - `test_duplicate_class_fails`;
  - `test_field_modifier_public`;
  - `test_default_field_modifier_internal`;
  - `test_template_names_not_fields`.

**`test_check_ui_rules.py`** (SHOULD)

- One failing fixture per rule R1–R7, each reported with its rule id.
- The clean tree passes.
- `DataContext.` inside a `RelativeSource` path does not trip R3.

### Smoke sections (Windows CI)

- `page-ids`, `capture-ocr`.
- Per palette: `pages({id})`, then `commands`, `preview-lease`, `autoapply` and `close-flush` (first palette only),
  then the other windows, then the binding-error assertion and `palette {id}`.
- Teardown: the screenshot count and the total time.

### Manual

M1–M3 and P1–P2 as listed under Acceptance criteria. Also a keyboard pass: the Tab order through each page matches
the pre-change build. A `UserControl` adds no tab stop, because `Focusable` defaults to false.

## Files touched

| Path | Change | Shared file? |
| --- | --- | --- |
| `src/Envoy.Core/Mvvm/{ObservableObject,RelayCommand,AsyncRelayCommand}.cs` | new | |
| `src/Envoy.Core/Phrases/PhrasePreview.cs`, `PhrasebookText.cs` | new | |
| `src/Envoy.Core/Languages/OcrPackInstall.cs` | new | |
| `src/Envoy.Core/Chat/PlayerListText.cs` | new | |
| `src/Envoy.Core/Translation/TranslationTestText.cs` | new | |
| `src/Envoy.Core/Ocr/CapturePreviewText.cs` | new | |
| `src/Envoy.Core/Hotkeys/{HotkeyStatus,HotkeyRegistrationResult,HotkeyText}.cs` | new (contract §2.3) | |
| `src/Envoy.Core/Threading/LeaseCounter.cs` | new | |
| `src/Envoy.Core/Text/TextTemplate.cs` | new (contract §8.7; every later `*Text` class uses it) | |
| `tests/Envoy.Core.Tests/{Mvvm,TextTemplate,LeaseCounter,HotkeyText,PhrasePreview,OcrPackInstall,PlayerListText,TranslationTestText,CapturePreviewText}Tests.cs` | new | |
| `src/Envoy.App/IShell.cs` | new: the 01 block only | **yes** (02, 06, 07a add blocks) |
| `src/Envoy.App/Services/DialogService.cs` | new: `ShowInfo`, `ShowWarning` | **yes** (02 adds `Confirm`) |
| `src/Envoy.App/Controls/AutoApply.cs` | new | |
| `src/Envoy.App/Services/SettingsAutoApplier.cs` | new | |
| `src/Envoy.App/Services/ChatWatcher.cs` | `PublishFrames` → `BeginPreview()`/`IsPreviewing` (3 lines plus the lease field) | **yes** (05 rewrites) |
| `src/Envoy.App/Services/ThemeManager.cs` | adds `PaletteIds` | **yes** (07b) |
| `src/Envoy.App/AppHost.cs` | adds `HotkeyResults` and `HotkeyResultsChanged` only | **yes** |
| `src/Envoy.App/App.xaml.cs` | `: IShell`; instance `OpenUrl`; `InstallOcrPack`; `ShowInfo`; `RegisterHotkeys` → `HotkeyResults`; `HotkeyErrors` removed; the unhandled-exception box goes through `DialogService` | **yes** |
| `src/Envoy.App/Windows/ComposeWindow.xaml.cs` | the send-failure `MessageBox.Show` → `DialogService.ShowInfo` (same text) | **yes** (02, 07c) |
| `src/Envoy.App/Pages/{WarRoom,Translation,ReadingChat,Overlay,Replying,Phrasebook,Hotkeys,Appearance,About}Page.xaml(.cs)` | new: XAML cut from `MainWindow.xaml`, with handlers swapped for commands | **yes** (every later UI area) |
| `src/Envoy.App/Pages/PageIds.cs` | new | |
| `src/Envoy.App/ViewModels/MainViewModel.cs` + `.{Translation,ReadingChat,Phrasebook,Actions}.cs` | constructor, `ObservableObject` base, commands, state, `ReportCommandError`, `Dispose`; split into partials | **yes** |
| `src/Envoy.App/ViewModels/{Option,OcrLanguageItem,ThemeOption,CapturePreviewViewModel}.cs` | moved or new | **yes** (04, 05 extend `CapturePreviewViewModel`) |
| `src/Envoy.App/Windows/MainWindow.xaml` | about 794 → about 90 lines: chrome, nav, 9 page hosts, `xmlns:p`, `c:AutoApply.Ignore` on `Nav` | **yes** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | about 231 → about 80 lines; adds `(AppHost, IShell)` constructor, `PageFor`, `PageNames => PageIds.All`, `SettingsAutoApplier` | **yes** |
| `src/Envoy.App/SmokeTest.cs` | three-phase shape, sections and timing, `PaletteIds` × `PageIds.All`, binding listener, 01 checks, compose warm-up and poll, temp-root cleanup | **yes** (every area) |
| `src/Envoy.Core/Settings/AppSettings.cs`, `src/Envoy.App/Interop/Native.cs` | **no change** | yes (untouched) |
| `tools/typecheck/gen_stubs.py` | base class, header, event-handler `nameof`, full-name files, duplicate detection, `x:FieldModifier` | |
| `tools/typecheck/check_ui_rules.py`, `test_gen_stubs.py`, `test_check_ui_rules.py`, `fixtures/**` | new | |
| `tools/typecheck/typecheck.sh` | unittest discovery first; runs `check_ui_rules.py` | **yes** (07b adds `check_a11y.py`) |
| `.github/workflows/build.yml` | smoke `WaitForExit(180000)` → `WaitForExit(600000)` only | **yes** (06, 08b) |
| `CLAUDE.md`, `docs/architecture.md`, `CHANGELOG.md` | see Design §7 | **yes** |

The estimated size is about 1,500 changed lines. Most of it is XAML moved verbatim. New C# and Python come to about
750 lines, plus about 450 lines of tests.

## Risks & open questions

- **Binding breakage across the UserControl boundary.** `ElementName` bindings to `Nav*` must stay in `MainWindow`.
  An `ElementName` inside a page may only target elements in that same page. REQ-UIF-19 makes CI fail on any
  breakage.
- **Existing binding errors.** Some may already exist, now that overlay, compose, lookup and the region selector are
  also covered (contract §6). The implementer fixes them if the fix is trivial and behaviour-neutral. Otherwise they
  go on the exact-message allowlist in `SmokeTest.cs`, and the PR records them. A blanket suppression is not allowed.
- **Data-binding tracing in release builds.** The `DataBindingSource` switch must be set explicitly. If the published
  single-file exe still traces nothing, the fallback is to also listen at `SourceLevels.Warning`. P2 confirms this on
  the first run.
- **Smoke time.** This step adds 5 screenshots per palette and four first-palette checks (about 3 s), and removes the
  fixed 2.5 s compose sleep from every palette. The net change is expected to be neutral or faster. The 600 s timeout
  and the 420 s/30 s warnings leave room for later areas.
- **Smoke host and the real data folder.** Until 02, `AppHost.DataFolder` is static, so the smoke host still reads
  `%APPDATA%\Envoy\phrases.json` and can write transcripts there, as it does today. This step deletes only its own
  temp root. 02 fixes this by giving every smoke host a temp `DataFolder` (contract §3).
- **Interim modal boxes.** The compose send-failure box and the unhandled-exception box remain modal, routed through
  `DialogService`, until 02 replaces them with a toast and the global handler (contract §2.2).
- **`HotkeyStatus.SkippedSafeMode`.** Contract §2.3 shows it in the final enum, but nothing in 01 can produce it. Under
  the §1 arrival rule ("no placeholder members"), 02 appends it when it adds safe mode (contract §4, step 10). The
  integrator should confirm this reading; including it in 01 instead costs one line.
- **Interim wording.** `TranslationTestText.Failure` still shows the raw exception message, and `HotkeyText` still
  says "taken" for an invalid combo. Both are today's text, and this area changes no wording. 03 (error kinds) and
  07a (cheat sheet and hints) own the new text under the §8.7 voice rule.
- **Pixel parity.** Moving the subtree into a `UserControl` adds one element to the visual tree. That should not change
  layout or rendering, because font hinting and layout rounding are inherited. The capture-preview content on the CI
  runner is non-deterministic and is excluded from the comparison. Sub-pixel anti-aliasing differences are accepted
  and noted in the PR.
- **Auto-apply via `Inherits`.** `AutoApply.Ignore` is inherited through the logical tree. A settings control placed
  by mistake under an ignored container stops auto-applying. The smoke `autoapply` check covers one positive and one
  negative case; M2 covers the rest.
- **The `App.Current` cast in `MainWindow(AppHost)`.** In the smoke test, `App.Current` is the real `App`. The
  `(AppHost, IShell)` constructor exists so that a future headless harness can pass another shell.
- **Event-name heuristic in `gen_stubs.py`.** A handler on an event outside `EVENT_NAMES` is not checked. That is a
  false negative only, because the Windows build still catches it. The list can grow as needed.
- **Open question.** Should `AsyncRelayCommand` expose cancellation (`CancellationToken`)? It is left out to stay
  minimal. It can be added later without breaking callers.
- **Deferred.** The palette-only colour lint for `Pages/*.xaml` (for example the `#40000000` swatch stroke on
  Appearance) belongs to 07b's `check_a11y.py` rule 4.

## Revision notes

Changes from the pre-review draft, made to comply with `00-integration.md` and the set critique:

- **MUST budget (contract §9).** The spec now has 15 MUSTs: UIF-1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 13, 15, 18, 19 and 21.
  - REQ-UIF-6 is folded into 5, and 12 into 8.
  - REQ-UIF-14 is now SHOULD.
  - REQ-UIF-24 moves to the Definition of done (Design §7).
  - REQ-UIF-21 (the preview lease) is promoted to MUST.
  - REQ-UIF-20, 22 and 23 are folded into REQ-UIF-18, 1 and 3, because contract §1 and §6 make every-page
    screenshots, the partial files and `PageIds` deliverables that later steps rely on. The MUST count is unchanged.
- **Major issue: `ShowInfo` used `MessageBox` in `App.xaml.cs`.** `Services/DialogService.cs` now owns every modal box.
  All three existing `MessageBox.Show` sites route through it with unchanged text, and `check_ui_rules.py` R5
  enforces this. `IShell.ShowInfo` now takes `(title, message)`, as in contract §3.
- **Major issue: `AsyncRelayCommand` swallowed errors.** `onError` is now required and `MainViewModel.ReportCommandError`
  is the single sink, which 02 rewires to the log and a toast (contract §8.8). The `NoOnError_Swallowed` test is
  replaced by `NullOnError_Throws`.
- **Minor issue: the single `PublishFrames` flag.** It is replaced by `ChatWatcher.BeginPreview()`, backed by a Core
  `LeaseCounter` with unit tests. The smoke `preview-lease` section covers the two-instance case (contract §2.8).
- **Minor issue: ACs hard-coded "5 themes" and "45 PNGs".** The ACs now name `ThemeManager.PaletteIds`, and the
  expected count is `PaletteIds.Count × PageIds.All.Count`, asserted by the smoke test itself (contract §6). 01 adds
  `PaletteIds` so the loop already has its final form; 07b appends high contrast.
- **Contract §1, §2.3 and §3 (`IShell`).** `IShell` is exactly the 01 block. `HotkeyErrors` is removed and
  `InstallOcrPack` and `OpenUrl(urlOrDataPath)` are added. The elevated PowerShell launch moves out of the view model
  into `App.InstallOcrPack`, ready for 08a's `ShellLauncher.InstallOcrPack`. `OcrPackInstall.PowerShellArguments` now
  takes the language code, matching contract §5.
- **Contract §2.3 (hotkey results).** New Core `HotkeyStatus`, `HotkeyRegistrationResult` and `HotkeyText`.
  `AppHost.HotkeyResults` and `HotkeyResultsChanged` are written only by `App.RegisterHotkeys`. `App.HotkeyErrors` is
  deleted. `SkippedSafeMode` is left to 02 under the arrival rule, and this is flagged under Risks.
- **Contract §6 (smoke).** The smoke test now has the three-phase structure with `[smoke] <section> ok elapsed_ms`
  lines. The binding listener now spans every window of a palette, not just `MainWindow`. The provider wait runs once
  in setup, and compose polls instead of sleeping 2.5 s per palette. The test logs budget warnings and deletes its
  temp root, and `build.yml` uses `WaitForExit(600000)`. The non-goal "no CI workflow changes" is narrowed to allow
  this one line.
- **Contract §8.7 (strings).** The moved user-facing text now lives in Core `*Text` classes: `PhrasebookText`,
  `CapturePreviewText`, `HotkeyText`, and `TranslationTestText` as format constants. The wording is unchanged.
- **Contract §7 and §8.8 (`CLAUDE.md` rules).** This area adds the Pages binding rule and the `AutomationProperties.Name`
  rule, plus the §8.7 string rule and the smoke-placement rule.
- **Verifiable MUSTs (contract §9).** Several ACs used to be greps or manual-only checks. They are now mechanical:
  - `check_ui_rules.py`, run by `typecheck.sh`, enforces the Pages, ViewModel and DialogService rules;
  - the smoke sections `autoapply`, `close-flush`, `commands`, `preview-lease` and `page-ids` cover the rest.

  The remaining manual checks are numbered: M1–M3 for 08b's release pass, and P1–P2 as one-off checks on this PR.
- **08a analyzers.** Stubs now carry `// <auto-generated/>`, so 08a's analyzers don't flag `__XamlEventHandlers`.
- **Allowlists (contract §5).** This area adds no P/Invoke, no network code and no new launch sites. That is now
  stated explicitly in Design §6.
- **Non-goals.** They now record the narrow `AppHost`, `ChatWatcher`, `ThemeManager` and `build.yml` edits that the
  contract assigns to this step. The merge-order risk is replaced by contract §1.

## Deviations

Differences between this spec (and the contract) and what was implemented, with the reasons.

- **`MainViewModel` constructor is `internal`.** `SettingsAutoApplier` is `internal` as specified, and a `public`
  constructor cannot take an `internal` parameter type (CS0051). The class and every member stay `public`.
- **Commands are created in the constructor in `MainViewModel.cs`.** Get-only properties can only be assigned in a
  constructor, so the `…Command` properties and their bodies live in `MainViewModel.Actions.cs`, but the assignments
  sit in the shared constructor.
- **Pages without their own partial.** State for Replying, Overlay, Hotkeys, Appearance and About stays in
  `MainViewModel.cs`, grouped by page comments; only the four partials named in Design §3 were created.
- **`MainViewModel.Dispose` also unsubscribes `Host.PropertyChanged`.** The old view model never did, so every closed
  settings window stayed reachable from the host. No visible change.
- **`TextTemplate.Format` is strict in both directions.** "An unknown or missing name throws" is read as: a
  placeholder with no value, a value the template never uses, a duplicate value name, and a malformed placeholder
  (`{}`, `{:N0}`, an unclosed `{` or a lone `}`) all throw `FormatException`. A null value formats as `""`.
- **`HotkeyText.TakenItemFormat` (`"{name} ({combo})"`).** Added so each list entry is a template rather than a
  concatenated fragment (contract §8.7). The resulting text is unchanged. `HotkeyText.ActionName` throws
  `ArgumentOutOfRangeException` for an unknown id, which is what `ActionName_CoversEveryHotkeySettingsProperty`
  relies on.
- **`App.InstallOcrPack`** catches `Win32Exception` and `ArgumentException` in one filter and opens
  `OcrPackInstall.SettingsFallbackUri` for both, as designed. `OcrPackInstall.CapabilityName` also rejects an empty
  tag.
- **`CapturePreviewViewModel` dispatcher.** `MainViewModel` passes `Dispatcher.CurrentDispatcher`, because its
  constructor signature (Design §3) has no dispatcher and `MainWindow` always builds it on the UI thread.
- **`gen_stubs.py`.** `EVENT_NAMES` has about 90 entries (the listed families plus `Indeterminate`,
  `DataContextChanged`, drop-down, context-menu, tool-tip and `Application` events), and `<EventSetter Handler="…">`
  also counts as a handler reference. Stubs carry `#nullable enable` after the `// <auto-generated/>` header, and
  files under `obj/` or `bin/` are skipped. A bad `x:FieldModifier` names the file and the field.
- **`check_ui_rules.py`.** R3 also flags a `<….DataContext>` property element, not just the attribute. Whole-line
  comments are ignored by R2, R4 and R5. R7 reports missing files as `path:0:`.
- **Typecheck test fixtures.** The stub fixtures are checked in under `tools/typecheck/fixtures/stubs/` (`ok/`, `dup/`,
  `weird/`, `badmodifier/`), because `generate()` works per folder. The `check_ui_rules` trees are built by
  `test_check_ui_rules.py` in a temp folder from `REQUIRED_FILES` instead of a checked-in `ui_rules/` tree of ~25
  placeholder files, so they cannot drift from the real file list. `TypeCheck.csproj` excludes `fixtures/**` from
  compilation. Extra cases: `test_bad_field_modifier_fails`, a second R1 (attached event), R2 and R3 case each, and an
  R7 case for `DialogService.cs`.
- **Smoke test details.**
  - Extra sections beyond Design §5: `host`, `ocr-languages`, `provider-warmup`, `close({id})`, `overlay({id})`,
    `compose({id})`, `lookup({id})`, `region-selector({id})`, `binding-errors({id})` and `teardown`; the last line is
    `[smoke] total elapsed_ms=<n>`.
  - The log is appended line by line instead of written at the end, so a run killed by the CI timeout still shows how
    far it got.
  - The warm-up resolves the reply language the way the reply box does and also warms the back-translation; the
    compose poll waits (within the same 2.5 s cap) for the back-translation too, so `{palette}-reply.png` keeps
    today's content.
  - `commands({id})` skips buttons that are parts of a control's own template (`TemplatedParent is Control`); none
    exist today.
  - `preview-lease({id})` also checks that an active second view model holds a lease and that disposing it releases
    the lease; `autoapply({id})` waits 600 ms before counting, so nothing scheduled earlier is miscounted.
  - The binding-error allowlist is empty. No errors were known when this landed; any that the first Windows run
    reports must be fixed or added by exact message in that PR.
  - New side effect: the `autoapply` and `close-flush` checks run `AppHost.ApplySettings()`, which calls
    `StartupRegistration.Apply(false)` with the temp settings. On a developer PC that runs `--smoke-test` this removes
    the current user's `Envoy` "run at sign-in" value if one exists. CI runners have none. 02's temp `DataFolder`
    work does not cover the registry; whoever touches `StartupRegistration` next should route it through the host.
- **Not verifiable in this environment.** The Linux box cannot build WPF, so AC-UIF-9 (P1 screenshot parity),
  AC-UIF-12/13 (Windows smoke run, P2 listener sanity) and the manual steps M1–M3 run on the first Windows CI build
  of this change. Everything else (unit tests, `typecheck.sh` including the Python suites and `check_ui_rules.py`)
  passes locally.
- **`HotkeyStatus.SkippedSafeMode`** is not added; 02 appends it with safe mode (arrival rule), as Risks proposed.
