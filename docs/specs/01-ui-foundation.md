# UI foundation: settings pages as UserControls, commands, and a typecheck that understands them

Status: Proposed

Area id: `01-ui-foundation` · Requirement prefix: `UIF` · Runs **first**, before every other area. This change must
not alter behaviour: the screenshots and settings file stay the same, and every button does what it does today.

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

- `src/Envoy.App/Windows/MainWindow.xaml.cs` (231 lines) has every handler above, plus:
  - **Auto-apply.** It listens to bubbling `TextChanged`, `SelectionChanged`, `Checked`, `Unchecked`, `ValueChanged`
    and `PasswordChanged` events and restarts a 400 ms `DispatcherTimer`. The timer calls `ApplyNow()`, which runs
    `AppHost.ApplySettings()`, `RefreshStatus()` and `RefreshProviderSections()`. Controls that are not settings are
    left out by **checking the instance** (`fe == TestInput || fe == PreviewText || fe == PhrasePreviewLanguage`) or
    by `RadioButton { GroupName: "Nav" }`.
  - **Live capture preview.** `NavCapture.Checked`/`Unchecked` toggles `Watcher.PublishFrames`, and `OnFrame` builds a
    `BitmapSource` into `PreviewImage`.
  - **Phrasebook preview.** `FillPhrases()` projects `PhraseBook.All` into a nested `PhraseRow` record. `OnEditPhrases`
    writes a template `phrases.json`, opens it, blocks on a `MessageBox`, then reloads.
  - `OnInstallOcr` builds a PowerShell `Add-WindowsCapability` command line inline, including the special case that
    `zh-CN`/`zh-TW` use the code instead of the OCR tag.
  - `OnTestTranslate` is `async void`, flushes pending edits and formats the result string inline.
  - `PageNames` (`Dashboard`, `Translation`, `Capture`, `Overlay`, `Reply`, `Phrases`, `Hotkeys`, `Appearance`, `About`)
    and `Navigate(string)` are used by `SmokeTest.cs`.
  - `OnClosed` flushes a pending apply, stops the preview, sets `FirstRunCompleted = true` and saves.
- `src/Envoy.App/ViewModels/MainViewModel.cs` (270 lines) is one class plus `Option`, `OcrLanguageItem` and
  `ThemeOption`. It has properties but no commands, and it reaches `App.Current` directly for `HotkeyErrors`.
- `src/Envoy.App/SmokeTest.cs` creates `MainWindow` under each of the 5 themes, navigates every page, and with
  `--screenshots` saves `{theme}-settings-{dashboard,translation,capture,appearance}.png`. CI publishes these to the
  `ci-screenshots` branch, and README/docs images come from them.
- `tools/typecheck/gen_stubs.py` writes a `partial class` stub for each `x:Class` XAML file, with an empty
  `InitializeComponent()` and one `internal` field per `x:Name` outside templates. Its limitations:
  - It emits **no base class**. A root element that disagrees with the code-behind (`<UserControl>` vs `: Window`)
    passes on Linux and fails only on Windows CI.
  - It writes `stubs/<ShortName>.g.cs`, so two XAML classes with the same short name in different namespaces overwrite
    each other silently.
  - It ignores `x:FieldModifier`.
  - It does **not check event-handler names**. Once handlers move into pages, a `Click="OnFoo"` with no `OnFoo` in the
    matching code-behind type-checks fine and only breaks the Windows build.

### What is weak for production

1. Every later area edits the same 800-line XAML file and the same code-behind, so merges conflict and review is hard.
2. Button logic lives in view code-behind. It can't be tested, and `async void` handlers crash the app on an unexpected
   exception: `OnTestTranslate` catches its own exceptions, but nothing enforces that pattern.
3. Small but real logic (OCR-pack command line, phrase preview projection, muted-player text parsing, test-translation
   text) sits in the App project, where Linux CI cannot test it.
4. The auto-apply exclusion list goes by field identity. Once controls move into separate pages, those fields no
   longer exist on `MainWindow`.
5. CI does not fail on WPF binding errors. Splitting XAML is exactly the kind of change that breaks bindings silently,
   for example `ElementName` across a namescope or a wrong `DataContext`.

## Goals / Non-goals

**Goals**

- One `UserControl` per settings page under `src/Envoy.App/Pages/`. All pages share the single `MainViewModel`
  instance that `MainWindow` creates.
- A small `ICommand` toolkit (`RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand`, `ObservableObject`) in
  `Envoy.Core`, with unit tests. Page buttons and hyperlinks run through commands on the view model.
- Move the logic hidden in `MainWindow.xaml.cs` into `Envoy.Core`, with tests. Behaviour stays byte-identical.
- The typecheck stub generator understands UserControls, base classes, field modifiers and event-handler names.
- The smoke test checks that every page loads, and fails on binding errors.
- A pixel-identical look: same window size, layout, styles, palette keys, and screenshot file names.

**Non-goals**

- No visual redesign, new settings, new pages, or changes to text or wording. Those belong to other areas.
- No MVVM framework dependency (CommunityToolkit.Mvvm, Prism and so on). The toolkit stays about 150 lines.
- No splitting of `MainViewModel` into several view-model classes. It stays one class, optionally split into partial files.
- No conversion of `OverlayWindow`, `ComposeWindow`, `LookupWindow` or `RegionSelectorWindow` to commands.
- No change to `AppSettings`, the settings JSON format, `AppHost` behaviour, threading of the capture loop, or CI workflow files.
- No lazy page loading or virtualisation. Pages are created eagerly, as the inline XAML is today.

## Requirements

| Id | Priority | Requirement |
| --- | --- | --- |
| REQ-UIF-1 | MUST | `MainWindow.xaml` contains only the window chrome, the sidebar navigation and nine page host elements. Each page's content lives in its own `UserControl` (`WarRoomPage`, `TranslationPage`, `ReadingChatPage`, `OverlayPage`, `ReplyingPage`, `PhrasebookPage`, `HotkeysPage`, `AppearancePage`, `AboutPage`) in namespace `Envoy.App.Pages` under `src/Envoy.App/Pages/`. |
| REQ-UIF-2 | MUST | No page sets its own `DataContext`. Every page inherits the single `MainViewModel` instance that `MainWindow` creates. |
| REQ-UIF-3 | MUST | `MainWindow.PageNames`, the strings `Navigate(string)` accepts, and the smoke-test screenshot file names stay exactly as they are today. |
| REQ-UIF-4 | MUST | An edit to any settings control on any page still triggers one debounced (400 ms) `AppHost.ApplySettings()` + `RefreshStatus()` + `RefreshProviderSections()`. Controls that are not settings opt out through an inheritable `AutoApply.Ignore` attached property instead of field identity. |
| REQ-UIF-5 | MUST | `Envoy.Core.Mvvm` provides `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand` (all `System.Windows.Input.ICommand`) and `ObservableObject`, covered by unit tests. |
| REQ-UIF-6 | MUST | While its task runs, `AsyncRelayCommand` reports `CanExecute == false` and `IsRunning == true` and ignores re-entrant `Execute` calls. An exception from the task goes to the supplied error handler and is never rethrown on the dispatcher. |
| REQ-UIF-7 | MUST | Every `Button` and `Hyperlink` inside `Pages/*.xaml` runs through a `Command` binding to a `MainViewModel` command. Page code-behind files contain no `Click`/`RequestNavigate` handlers. |
| REQ-UIF-8 | MUST | Each command does the same thing as the handler it replaces, including flushing pending edits before "Translate" (test) and "Test" (voice). |
| REQ-UIF-9 | MUST | View-model commands call app-level actions (select region, toggle overlay edit mode, open compose, scan, quit, open URL, show info box) through an `IShell` interface implemented by `App`, not through `App.Current`. |
| REQ-UIF-10 | MUST | The logic currently in `MainWindow.xaml.cs` moves to `Envoy.Core` with unit tests and identical output: phrase-preview rows and the user-phrases template (`PhrasePreview`), the OCR pack capability name and PowerShell arguments (`OcrPackInstall`), muted-player text parse/format (`PlayerListText`), and test-translation result text (`TranslationTestText`). |
| REQ-UIF-11 | MUST | Settings screenshots produced by `--screenshots` for the existing four pages under all five themes match the pre-change renders. The only allowed difference is the live capture-preview image and text. |
| REQ-UIF-12 | MUST | Closing `MainWindow` still applies a pending edit, stops frame publishing, sets `FirstRunCompleted = true` and saves. |
| REQ-UIF-13 | MUST | `gen_stubs.py` generates a correct stub for `UserControl` roots, and for any root it knows. The stub declares the WPF base class of the root element, so a mismatch with the code-behind fails the typecheck. An unknown root element is a hard error. |
| REQ-UIF-14 | MUST | `gen_stubs.py` names stub files by full class name and fails when two XAML files declare the same `x:Class`. |
| REQ-UIF-15 | MUST | `gen_stubs.py` emits a compile-time reference (`nameof`) for every event-handler attribute in a XAML file, including ones inside templates. A handler name missing from the class makes `typecheck.sh` fail. |
| REQ-UIF-16 | SHOULD | `gen_stubs.py` honours `x:FieldModifier` (`public`, `internal`, `private`, `protected`) and defaults to `internal`. |
| REQ-UIF-17 | SHOULD | `tools/typecheck/test_gen_stubs.py` (stdlib `unittest`, fixture XAML) covers REQ-UIF-13..16, and `typecheck.sh` runs it before generating stubs. |
| REQ-UIF-18 | MUST | For each theme, the smoke test checks that after `Navigate(name)` the matching page is a loaded, visible instance of the expected `Pages` type with non-zero `ActualHeight`, and that every other page is collapsed. |
| REQ-UIF-19 | MUST | The smoke test fails when WPF raises any data-binding error (`PresentationTraceSources.DataBindingSource` at `Error` level) while it builds and navigates `MainWindow`. It logs each error. |
| REQ-UIF-20 | SHOULD | With `--screenshots`, the smoke test also saves the other five pages (`overlay`, `reply`, `phrases`, `hotkeys`, `about`) per theme, using the same `{theme}-settings-{page}.png` pattern. |
| REQ-UIF-21 | SHOULD | Capture-preview state (`Image`, `Text`, `Stats`, `IsActive`) moves to a `CapturePreviewViewModel`. Frames are published only while `ReadingChatPage` is visible, and the model unsubscribes from `FrameCaptured` when `MainWindow` closes. |
| REQ-UIF-22 | SHOULD | `MainViewModel` is split into partial-class files per page, and `Option`, `OcrLanguageItem` and `ThemeOption` move to their own files, so later areas edit separate files. |
| REQ-UIF-23 | SHOULD | `Pages/PageIds.cs` exposes the page names as `const string`s, and `MainWindow.PageNames` plus the smoke test use them. |
| REQ-UIF-24 | MUST | `docs/architecture.md`, `CLAUDE.md` (the "Settings changes" rule) and `CHANGELOG.md` (an "Internal" line) describe the Pages/commands structure. |

## Design

### 1. Command toolkit: `src/Envoy.Core/Mvvm/`

`System.Windows.Input.ICommand` lives in the BCL (`System.ObjectModel`), so these types build on `net8.0`. They are
tested on Linux and WPF binds to them unchanged. They do **not** use WPF's `CommandManager.RequerySuggested`, because
that type is WPF-only. Callers call `RaiseCanExecuteChanged()` explicitly.

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
    // parameter that is not T (and not null) => CanExecute false, Execute no-op (no InvalidCastException).
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter);
    public void Execute(object? parameter);
    public void RaiseCanExecuteChanged();
}

public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null);
    public bool IsRunning { get; }                      // raises PropertyChanged(nameof(IsRunning))
    public bool CanExecute(object? parameter);          // !IsRunning && (canExecute?.Invoke() ?? true)
    public void Execute(object? parameter);             // fire-and-forget wrapper over ExecuteAsync
    public Task ExecuteAsync();                         // what tests await; never throws (error -> onError, or swallowed + Debug.WriteLine if onError is null)
    public event EventHandler? CanExecuteChanged;       // raised when IsRunning flips and on RaiseCanExecuteChanged()
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseCanExecuteChanged();
}
```

**Threading.** `ExecuteAsync` awaits **without** `ConfigureAwait(false)`, so when a WPF button calls it on the UI
thread, `IsRunning`/`CanExecuteChanged` changes and `onError` all run back on the UI thread. Tests run with no
synchronisation context and get thread-pool continuations, which is fine for assertions. `OperationCanceledException`
is treated like any other exception and goes to `onError`.

### 2. Logic moved to Core (behaviour-identical)

```csharp
namespace Envoy.Core.Phrases;
public sealed record PhrasePreviewRow(string Icon, string Category, string Mine, string Theirs);
public static class PhrasePreview
{
    public const string Missing = "—";
    /// Mine = p.In(myLanguage) ?? p.In("en") ?? p.Id; Theirs = p.In(targetLanguage) ?? Missing. Order = book order.
    public static IReadOnlyList<PhrasePreviewRow> Build(PhraseBook book, string myLanguage, string targetLanguage);
    /// The exact JSON written today by OnEditPhrases when phrases.json doesn't exist.
    public const string UserFileTemplate = """ ...current literal... """;
    public const string EditInstructions =
        "Your phrases file is open. Save it, then press OK to reload.\n\nUse the same \"id\" as a built-in phrase to replace it.";
}

namespace Envoy.Core.Languages;
public static class OcrPackInstall
{
    public const string SettingsFallbackUri = "ms-settings:regionlanguage";
    /// "Language.OCR~~~{tag}~0.0.1.0" where tag = Code for zh-CN/zh-TW, else language.OcrTag.
    /// Throws ArgumentException if the tag contains anything other than letters, digits and '-'
    /// (the value is spliced into a PowerShell command run elevated).
    public static string CapabilityName(Language language);
    /// The exact argument string passed to powershell.exe today:
    /// -NoProfile -Command "Add-WindowsCapability -Online -Name '<cap>'; Write-Host 'Done - restart Envoy to use it.'; Start-Sleep 4"
    public static string PowerShellArguments(Language language);
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
    /// "{Text}    ({NameOf(detected)} → {NameOf(myLanguage)}, via {ProviderCatalog name ?? ProviderId})"
    public static string Describe(TranslationResult result, string myLanguage);
    public static string Failure(Exception ex);                 // "⚠ " + ex.Message
}
```

`MainViewModel.MutedPlayersText` becomes `get => PlayerListText.Format(Settings.MutedPlayers); set =>
Settings.MutedPlayers = PlayerListText.Parse(value);`.

### 3. App-side infrastructure

**`src/Envoy.App/IShell.cs`** (new)

```csharp
namespace Envoy.App;
/// <summary>App-level actions the view models may trigger. Implemented by App.</summary>
public interface IShell
{
    void SelectRegion();
    void ToggleOverlayEditMode();
    void OpenCompose();
    void ScanAndTranslate();
    void ExitApp();
    void OpenUrl(string url);
    /// <summary>Blocking information box, owned by the main window when it is open.</summary>
    void ShowInfo(string message);
    IReadOnlyList<string> HotkeyErrors { get; }
}
```

`App` gets `: IShell`. Most members already exist with these exact signatures (`SelectRegion`, `ScanAndTranslate`
and `OpenCompose` stay `async void`/`void`). The new members are:

- `void IShell.OpenUrl(string url) => OpenUrl(url);`, because the existing static `App.OpenUrl` stays for other callers.
- `public void ShowInfo(string message)`, which calls
  `MessageBox.Show(_main, message, "Envoy", MessageBoxButton.OK, MessageBoxImage.Information)` when `_main` is non-null,
  and otherwise the ownerless overload.

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
- `TranslationPage` test input;
- `ReadingChatPage` preview text box;
- `PhrasebookPage` preview-language ComboBox.

`MainWindow.OnEdited` becomes
`if (e.OriginalSource is DependencyObject d && AutoApply.GetIgnore(d)) return; if (!IsLoaded) return; _applier.Schedule();`.

**`src/Envoy.App/Services/SettingsAutoApplier.cs`** (new; wraps the existing `DispatcherTimer` logic)

```csharp
internal sealed class SettingsAutoApplier
{
    public SettingsAutoApplier(Dispatcher dispatcher, TimeSpan delay, Action apply); // delay = 400 ms (unchanged)
    public bool IsPending { get; }
    public void Schedule();        // (re)start the timer
    public void ApplyNow();        // stop timer, apply unconditionally (used before Test translate / Test voice)
    public void FlushIfPending();  // apply only if a change is pending (used on window close)
}
```

The `apply` action is the old `ApplyNow` body: `host.ApplySettings(); vm.RefreshStatus(); vm.RefreshProviderSections();`.
`MainWindow` creates it and passes it to `MainViewModel`.

**`MainViewModel`** keeps its public surface and gains a constructor, commands and new state.

- **Constructor:** `MainViewModel(AppHost host, IShell shell, SettingsAutoApplier applier)`. `MainWindow(AppHost host)`
  keeps its signature and passes `(IShell)App.Current`.
- **Base class:** it derives from `ObservableObject`. `public void Notify([CallerMemberName] string? name = null)` stays
  as a wrapper over `OnPropertyChanged`.
- **`HotkeyErrors`** reads `_shell.HotkeyErrors` instead of `App.Current`.
- **New commands**, all get-only properties created in the constructor:

| Command | Type | Replaces | Body |
| --- | --- | --- | --- |
| `SelectRegionCommand` | `RelayCommand` | `OnSelectRegion` (War Room, Reading Chat) | `_shell.SelectRegion()` |
| `ToggleCaptureCommand` | `RelayCommand` | `OnToggleCapture` | `Host.ToggleCapture()` |
| `ShowSampleMessagesCommand` | `RelayCommand` | `OnDemo` (War Room, Overlay) | `Host.ShowDemoMessages()` |
| `MoveOverlayCommand` | `RelayCommand` | `OnEditOverlay` (War Room, Overlay) | `_shell.ToggleOverlayEditMode()` |
| `OpenReplyBoxCommand` | `RelayCommand` | `OnCompose` | `_shell.OpenCompose()` |
| `ScanAndTranslateCommand` | `RelayCommand` | `OnScan` | `_shell.ScanAndTranslate()` |
| `NewMatchCommand` | `RelayCommand` | `OnNewMatch` | `Host.NewMatch()` |
| `DownloadUpdateCommand` | `RelayCommand` | `OnDownloadUpdate` | `if (Host.Update is { } u) _shell.OpenUrl(u.PageUrl)` (CanExecute stays true; the banner is only visible when an update exists, so there is no greyed-out look change) |
| `TestTranslateCommand` | `AsyncRelayCommand` | `OnTestTranslate` | `_applier.ApplyNow(); TranslationTestOutput = Pending; try { … Describe(…) } catch (Exception ex) { TranslationTestOutput = Failure(ex); }` |
| `InstallOcrCommand` | `RelayCommand<string>` | `OnInstallOcr` (parameter = language code) | `Process.Start(new ProcessStartInfo("powershell.exe", OcrPackInstall.PowerShellArguments(lang)) { UseShellExecute = true, Verb = "runas" })`; on `Win32Exception` it calls `_shell.OpenUrl(SettingsFallbackUri)` |
| `TestVoiceCommand` | `RelayCommand` | `OnTestVoice` | `_applier.ApplyNow(); Host.TestVoice();` |
| `EditPhrasesCommand` | `RelayCommand` | `OnEditPhrases` | write `PhrasePreview.UserFileTemplate` if missing → `_shell.OpenUrl(path)` → `_shell.ShowInfo(EditInstructions)` → `Host.ReloadPhrases()` → `RefreshPhrasePreview()` |
| `OpenLogsCommand` / `OpenDataFolderCommand` | `RelayCommand` | `OnOpenLogs` / `OnOpenData` | `Directory.CreateDirectory(folder); _shell.OpenUrl(folder)` |
| `OpenLinkCommand` | `RelayCommand<string>` | `OnLink` | `_shell.OpenUrl(url)` (the Hyperlink keeps `NavigateUri` for tooltip/accessibility and binds `CommandParameter` to the same literal) |
| `QuitCommand` | `RelayCommand` | `OnQuit` | `_shell.ExitApp()` |

- **New state:**
  - `string TranslationTestInput` (default `TranslationTestText.SampleInput`)
  - `string TranslationTestOutput` (default `""`)
  - `Language? PhrasePreviewLanguage`, defaulting to
    `LanguageCatalog.Find(Host.Pipeline.LastOpponentLanguage) ?? LanguageCatalog.Find("zh-CN")`. Its setter calls
    `RefreshPhrasePreview()`.
  - `IReadOnlyList<PhrasePreviewRow> PhraseRows`
  - `void RefreshPhrasePreview()`
  - `CapturePreviewViewModel CapturePreview`
- **Disposal:** `void Dispose()` disposes `CapturePreview`. `MainWindow.OnClosed` calls it.

All command bodies run on the UI thread, as the click handlers did. No command adds a new failure mode. Exceptions
that escaped a click handler before still escape the `RelayCommand`, so `App`'s existing dispatcher-unhandled
handling sees them as before. The only exception is `TestTranslateCommand`, which routes through `onError`; its body
already catches everything.

**`src/Envoy.App/ViewModels/CapturePreviewViewModel.cs`** (new; replaces `SetPreview`/`OnFrame`)

```csharp
public sealed class CapturePreviewViewModel : ObservableObject, IDisposable
{
    public CapturePreviewViewModel(AppHost host, Dispatcher dispatcher); // subscribes host.Watcher.FrameCaptured
    public bool IsActive { get; set; }   // true: Watcher.PublishFrames = true + Invalidate(); false: PublishFrames = false
    public ImageSource? Image { get; }   // frozen BitmapSource built on the UI thread (same code as OnFrame)
    public string Text { get; }          // "(no text found)" or lines joined by NewLine
    public string Stats { get; }         // "{n} lines · OCR {ms:0} ms"
    public void Dispose();               // unsubscribe + PublishFrames = false
}
```

`ReadingChatPage.xaml.cs` holds the page's only code-behind logic:

```csharp
IsVisibleChanged += (_, _) => { if (DataContext is MainViewModel vm) vm.CapturePreview.IsActive = IsVisible; };
```

Visibility follows `NavCapture.IsChecked`, so the preview starts and stops at the same moments as before. There is
one intended internal difference: frames also stop while the main window is hidden to the tray. Nothing is drawn in
that state, so the user sees no change.

**`src/Envoy.App/Pages/`** (new). Each page is a `*.xaml` + `*.xaml.cs` pair. Code-behind is just
`public partial class XPage : UserControl { public XPage() => InitializeComponent(); }`, except `ReadingChatPage`
as described above.

- The page root content is the **exact** `ScrollViewer Padding="32,24"` subtree cut from `MainWindow.xaml`. The
  `Visibility` binding moves out to the host element, and `MaxWidth` stays 900, or 760 for About.
- Each page XAML declares only the namespaces it uses (`x`, the default namespace, and `c` for `Controls` where
  `HotkeyBox`/`SecretBox`/`AutoApply` appear).
- `StaticResource`/`DynamicResource` keys resolve from `App.xaml` merged dictionaries as before.
- The `InstallOcr` button in the OCR `DataTemplate` binds
  `Command="{Binding DataContext.InstallOcrCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
  CommandParameter="{Binding Code}"`.
- The phrase list binds `ItemsSource="{Binding PhraseRows}"`. The preview `ComboBox` binds
  `SelectedItem="{Binding PhrasePreviewLanguage}"`.
- `TestInput`/`TestOutput` bind to `TranslationTestInput` (`UpdateSourceTrigger=PropertyChanged`) and
  `TranslationTestOutput`. Preview controls bind to `CapturePreview.Image/Text/Stats`. No `x:Name` fields are
  needed in pages, but they are allowed.

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

- The title bar and sidebar are unchanged, plus `c:AutoApply.Ignore="True"` on `Nav`.
- The page grid holds nine hosts, each like
  `<p:WarRoomPage x:Name="WarRoomPage" Visibility="{Binding IsChecked, ElementName=NavDashboard, Converter={StaticResource BoolToVisibility}}" />`.
  These `ElementName` bindings stay inside `MainWindow`'s namescope, so they still resolve.

**`MainWindow.xaml.cs`** after the split (about 80 lines) keeps:

- the constructor, which creates the VM and applier, starts the status timer and registers the bubbling edit handlers;
- `PageNames => PageIds.All`;
- `Navigate`;
- the new `public UserControl PageFor(string name)`, a switch to the named host used by the smoke test;
- `OnEdited`;
- the window chrome handlers `OnMinimize`/`OnMaximize`/`OnClose`, which stay as view handlers;
- `OnClosed`, which runs `_statusTimer.Stop(); _applier.FlushIfPending(); _vm.Dispose(); FirstRunCompleted = true; SaveNow();`.

**`MainViewModel` file split (SHOULD).** `ViewModels/MainViewModel.cs` keeps the shared part: constructor, `Host`,
`Settings`, `RefreshStatus` and `Notify`. Page sections go in `MainViewModel.Translation.cs`,
`MainViewModel.ReadingChat.cs`, `MainViewModel.Phrasebook.cs` and `MainViewModel.Actions.cs` (the commands). Helper
types go in `ViewModels/Option.cs`, `OcrLanguageItem.cs` and `ThemeOption.cs`. Moving code does not change any
member signature.

### 4. Typecheck stub generator (`tools/typecheck/gen_stubs.py`)

- **Base class.** A map from root element to base type:
  `{Window: System.Windows.Window, UserControl: System.Windows.Controls.UserControl, Application: System.Windows.Application, Page: System.Windows.Controls.Page, ResourceDictionary: System.Windows.ResourceDictionary}`.
  The stub becomes `public partial class {name} : {base}`, matching what the WPF markup compiler emits. An unmapped
  root prints `"{path}: unsupported x:Class root <{tag}>"` and exits 1.
- **Stub file names** are `{namespace}.{name}.g.cs`. A second XAML file with an already-seen `x:Class` makes it exit 1
  with both paths.
- **Field modifiers.** `x:FieldModifier` (case-insensitive `public|internal|private|protected|protected internal`)
  sets the field's modifier. Anything else is an error.
- **Event handlers.**
  - Matched attributes have no namespace, a local name (or the part after the last `.`, for attached events such as
    `ButtonBase.Click`) in a fixed `EVENT_NAMES` set, and a value matching `^[A-Za-z_][A-Za-z0-9_]*$`.
  - `EVENT_NAMES` is about 50 entries: Click, Checked, Unchecked, Loaded, Unloaded, SelectionChanged, TextChanged,
    ValueChanged, PasswordChanged, RequestNavigate, Mouse*/PreviewMouse*, Key*/PreviewKey*, Got/LostFocus,
    Got/LostKeyboardFocus, SizeChanged, IsVisibleChanged, Closing, Closed, Activated, Deactivated, ContentRendered,
    SourceInitialized, StateChanged, LocationChanged, DragStarted/DragDelta/DragCompleted, ScrollChanged,
    Expanded/Collapsed, Opened, Drop/DragOver/DragEnter/DragLeave.
  - For each match, the stub gets a line `_ = nameof({value});` inside a
    `private void __XamlEventHandlers() { … }` method. Templates are included, because the real markup compiler
    wires template handlers to the root class too. A missing handler then fails with CS0103 in `dotnet build`.
- **Tests.** `tools/typecheck/test_gen_stubs.py` uses `unittest` with fixtures in `tools/typecheck/fixtures/`. It
  runs `gen_stubs` into a temp directory and asserts on the generated text. `typecheck.sh` runs
  `python3 -m unittest discover -s "$here" -p 'test_*.py'` first.
- **Unchanged.** `check_resources.py` already globs `**/*.xaml`, so it covers `Pages/` with no change. The
  `TypeCheck.csproj` compile glob already includes `src/Envoy.App/**/*.cs`, so it needs no change either.

### 5. Smoke test additions (`src/Envoy.App/SmokeTest.cs`)

- **Binding errors.** Before the theme loop, set `PresentationTraceSources.Refresh();` and
  `PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;`. Add a `BindingErrorListener : TraceListener`
  (a private nested class) that collects `WriteLine` messages.
  - After each theme's `MainWindow` pass, any collected message is logged as `binding error ({theme}): {message}` and
    then thrown as `InvalidOperationException`, which fails the run.
  - The listener is removed before the overlay, compose and lookup windows are built. Those windows are out of
    scope; a later area can widen the check.
- **Page checks.** Replace `foreach (var page in main.PageNames) main.Navigate(page);` with a loop that calls
  `Navigate(page)`, then `await Pump()`, then asserts that `main.PageFor(page)` has `IsLoaded`, `IsVisible` and
  `ActualHeight > 0`, and that `PageFor(other).Visibility == Collapsed` for every other page. A failure throws with
  the theme and page name.
- **More screenshots.** With `--screenshots`, the page list for screenshots becomes `PageIds.All`. The four existing
  file names are unchanged, and five new names are added.

### Settings added

None. `AppSettings.cs` and the settings JSON are unchanged.

### Failure behaviour

- An exception in a sync command propagates exactly as the old click handler's did, to the existing
  `DispatcherUnhandledException` path.
- An exception in `TestTranslateCommand` is caught and shown as `⚠ message`, as today. The command also guards
  against double-clicks while a test translation is running. That is a small, invisible improvement: the button greys
  out via the existing `IsEnabled=false` style while running.
- If the elevation prompt for `InstallOcrCommand` is declined, it opens Windows language settings, as today. If
  `CapabilityName` rejects an unexpected tag, that is an `ArgumentException`. It is unreachable with the built-in
  catalog, and the `catch` in the command routes it to the settings fallback too.

## Acceptance criteria

| Id | Maps to | Verified by |
| --- | --- | --- |
| AC-UIF-1 | REQ-UIF-1, REQ-UIF-2 | `tools/typecheck/typecheck.sh` passes. `src/Envoy.App/Pages/` has exactly the nine `*.xaml`/`*.xaml.cs` pairs. `grep -c 'ScrollViewer' src/Envoy.App/Windows/MainWindow.xaml` is `0`. `grep -n 'DataContext' src/Envoy.App/Pages/*.xaml` only matches `DataContext.` inside `RelativeSource` command bindings. The Windows CI build passes. |
| AC-UIF-2 | REQ-UIF-3, REQ-UIF-23 | The smoke test produces `{theme}-settings-dashboard/translation/capture/appearance.png` with unchanged names (CI artifact listing). `MainWindow.PageNames` returns `PageIds.All` (typecheck). |
| AC-UIF-3 | REQ-UIF-4 | Manual, Windows: change "Main provider" on Translation and a hotkey on Hotkeys, wait 1 s, close Envoy, and confirm both appear in `%APPDATA%\Envoy\settings.json`. Type in the Translation test box, switch nav pages, and change the phrase preview language. With a file watcher, or by checking that the file's modified time is unchanged, confirm none of these triggers a settings save. |
| AC-UIF-4 | REQ-UIF-5, REQ-UIF-6 | Unit tests in `tests/Envoy.Core.Tests/MvvmTests.cs` (listed in the test plan) pass under `dotnet test`. |
| AC-UIF-5 | REQ-UIF-7 | Typecheck passes. `grep -nE 'Click=\|RequestNavigate=' src/Envoy.App/Pages/*.xaml` returns nothing. `grep -n 'RoutedEventArgs' src/Envoy.App/Pages/*.cs` returns nothing. |
| AC-UIF-6 | REQ-UIF-8, REQ-UIF-9 | Manual, Windows, one pass over every button. In each case the result matches the pre-change build: War Room Select chat area / Pause-resume / Show sample messages / Move the overlay / Open reply box / Scan & translate / New match; Translation "Translate" test (shows `…` then result), the two "Get a key" links open the browser; Reading Chat Select chat area, Install (declining UAC opens Language settings); Overlay Drag to place / Show sample messages / Test voice; Phrasebook Edit my phrases (opens file, info box owned by main window, list reloads); Appearance Open logs; About Open settings folder / Quit Envoy. `grep -n 'App.Current' src/Envoy.App/ViewModels/*.cs` returns nothing (typecheck run). |
| AC-UIF-7 | REQ-UIF-10 | Unit tests in `PhrasePreviewTests.cs`, `OcrPackInstallTests.cs`, `PlayerListTextTests.cs` and `TranslationTestTextTests.cs` pass. |
| AC-UIF-8 | REQ-UIF-11 | Manual CI comparison. Download the `screenshots` artifact from the last `main` run before the change and from the PR run. For each of the 20 existing settings PNGs (5 themes × 4 pages), confirm identical dimensions and no visible difference outside the capture preview box, by flipping between them or with `compare` from ImageMagick if available. Record the result in the PR description. |
| AC-UIF-9 | REQ-UIF-12, REQ-UIF-21 | Manual, Windows: open Reading Chat and see the live preview update. Switch to Overlay; the preview stops, and the Scouts card shows normal capture status. Edit a setting and immediately close the window (X then Quit from tray); the edit is in `settings.json`. Reopen: no first-run behaviour. The smoke test also covers this, because closing `MainWindow` in each theme must not throw. |
| AC-UIF-10 | REQ-UIF-13, REQ-UIF-14, REQ-UIF-15, REQ-UIF-16, REQ-UIF-17 | `python3 -m unittest discover -s tools/typecheck -p 'test_*.py'` passes. As a one-off manual negative check, temporarily rename `OnMinimize` in `MainWindow.xaml.cs` or change a page root to `<Window>`, run `typecheck.sh`, confirm it fails, then revert. |
| AC-UIF-11 | REQ-UIF-18, REQ-UIF-19 | Windows CI smoke test passes. `smoke.log` contains `theme {id}: ok` for all 5 themes and no `binding error` lines. |
| AC-UIF-12 | REQ-UIF-20 | The CI `screenshots` artifact contains 45 `*-settings-*.png` files (5 themes × 9 pages). |
| AC-UIF-13 | REQ-UIF-22 | Typecheck passes. `ViewModels/` contains the partial and helper files listed in Design §3. |
| AC-UIF-14 | REQ-UIF-24 | Review: `docs/architecture.md` lists `Pages/` and `Mvvm/`; the `CLAUDE.md` settings rule says "bind it on the relevant page in `src/Envoy.App/Pages/`"; `CHANGELOG.md` has the entry. |

## Test plan

### Unit tests (`tests/Envoy.Core.Tests`, xUnit)

**`MvvmTests.cs`**

- `RelayCommand_Execute_InvokesAction`: the action runs once per `Execute`.
- `RelayCommand_CanExecuteFalse_ExecuteIsNoOp`: when the predicate returns false, the action does not run.
- `RelayCommand_RaiseCanExecuteChanged_RaisesEvent`: the event is raised exactly once with `sender == command`.
- `RelayCommandT_PassesTypedParameter`: `Execute("ko")` delivers `"ko"`.
- `RelayCommandT_WrongParameterType_CanExecuteFalseAndNoThrow`: `Execute(42)` on `RelayCommand<string>` neither throws
  nor invokes.
- `AsyncRelayCommand_WhileRunning_CanExecuteFalseAndIsRunningTrue`: uses a `TaskCompletionSource` to hold the task.
- `AsyncRelayCommand_ReentrantExecute_Ignored`: a second `Execute` during the run does not start the delegate again
  (counter stays 1).
- `AsyncRelayCommand_RaisesCanExecuteChanged_OnStartAndFinish`: the event fires at least twice.
- `AsyncRelayCommand_Exception_RoutedToOnError_NotThrown`: `await ExecuteAsync()` completes, `onError` receives the
  exception, and afterwards `IsRunning` is false and `CanExecute` is true.
- `AsyncRelayCommand_NoOnError_ExceptionSwallowed`: completes without throwing.
- `ObservableObject_SetProperty_RaisesOnlyOnChange`: setting the same value returns false with no event; a new value
  returns true with one event whose name is correct.

**`PhrasePreviewTests.cs`**

- `Build_UsesMyLanguageThenEnglishThenId`: covers a phrase with only `en`, a phrase with `my` set, and a phrase with
  neither.
- `Build_MissingTargetShowsDash`: `Theirs == "—"`.
- `Build_PreservesBookOrder`.
- `UserFileTemplate_LoadsAsUserPhrase`: write the template to a temp file, call `PhraseBook.LoadWithUserFile`, and
  find id `my-first-phrase` with `ko` text `만나서 반가워요!`.

**`OcrPackInstallTests.cs`**

- `CapabilityName_ChineseUsesCode`: `zh-CN` gives `Language.OCR~~~zh-CN~0.0.1.0`; the same for `zh-TW`.
- `CapabilityName_OthersUseCatalogOcrTag`: for every catalog language except `zh-*`, the name equals
  `$"Language.OCR~~~{lang.OcrTag}~0.0.1.0"`. This asserts against the catalog rather than hard-coded tags.
- `CapabilityName_RejectsUnsafeTag`: a `Language` with `OcrTag = "en'; rm"` throws `ArgumentException`.
- `PowerShellArguments_MatchesLegacyString`: an exact string equality for `ko` against the pre-change format.

**`PlayerListTextTests.cs`**

- `Parse_SplitsOnCommaSemicolonNewlines`.
- `Parse_TrimsAndDropsEmpty`.
- `Parse_DistinctIgnoreCase_FirstSpellingWins`.
- `Parse_Null_ReturnsEmpty`.
- `Format_JoinsWithCommaSpace`.
- `RoundTrip_FormatThenParse_Identity`.

**`TranslationTestTextTests.cs`**

- `Describe_KnownProvider_UsesDisplayName`: provider `google-free` gives the expected
  `"hello    (Chinese (Simplified) → English, via Google Translate (free))"`. Build the expected value from
  `LanguageCatalog.NameOf` and `ProviderCatalog.Find(..).Name` so catalog naming is not hard-coded.
- `Describe_UnknownProvider_FallsBackToId`.
- `Describe_NullDetected_UsesNameOfNull`.
- `Failure_PrefixesWarning`.

### Typecheck-tool tests (`tools/typecheck/test_gen_stubs.py`, stdlib unittest)

Fixtures live in `tools/typecheck/fixtures/`: `SampleControl.xaml` (a UserControl with a named element, an
`x:FieldModifier="public"` element, and a `Click` inside a `DataTemplate`), `Dup1.xaml`/`Dup2.xaml` with the same
`x:Class`, and `Weird.xaml` with a `<Canvas x:Class>` root.

- `test_usercontrol_base_class`: the stub contains `: System.Windows.Controls.UserControl`.
- `test_file_named_by_full_class`.
- `test_field_modifier_public`.
- `test_default_field_modifier_internal`.
- `test_template_names_not_fields`.
- `test_event_handlers_emitted_including_templates`: `nameof(OnRowClick)` is present.
- `test_attached_event_name`: `ButtonBase.Click="OnX"` counts.
- `test_binding_values_not_treated_as_handlers`: `Click="{Binding X}"` is not emitted.
- `test_duplicate_class_fails`: nonzero exit.
- `test_unsupported_root_fails`: nonzero exit.

### Smoke test additions

- A binding-error listener, with each error logged and failing the run (§5).
- Per-page load and visibility assertions in every theme (§5).
- Screenshots of all nine pages.

### Manual checks (Windows, real game optional)

- AC-UIF-3, AC-UIF-6, AC-UIF-8 and AC-UIF-9 as written above.
- A keyboard pass: Tab order through each page matches the pre-change build. A UserControl adds no tab stop
  (`IsTabStop` defaults to false and `Focusable` to false for UserControl).
- In game (any AoE title, windowed fullscreen): with Envoy reading chat, open the settings window over the game,
  switch pages, and close it. Capture keeps running and the overlay is unaffected.

## Files touched

| Path | Change | Shared file? |
| --- | --- | --- |
| `src/Envoy.Core/Mvvm/ObservableObject.cs` | new | |
| `src/Envoy.Core/Mvvm/RelayCommand.cs` | new: `RelayCommand`, `RelayCommand<T>` | |
| `src/Envoy.Core/Mvvm/AsyncRelayCommand.cs` | new | |
| `src/Envoy.Core/Phrases/PhrasePreview.cs` | new: rows, template, instructions | |
| `src/Envoy.Core/Languages/OcrPackInstall.cs` | new | |
| `src/Envoy.Core/Chat/PlayerListText.cs` | new | |
| `src/Envoy.Core/Translation/TranslationTestText.cs` | new | |
| `tests/Envoy.Core.Tests/MvvmTests.cs`, `PhrasePreviewTests.cs`, `OcrPackInstallTests.cs`, `PlayerListTextTests.cs`, `TranslationTestTextTests.cs` | new | |
| `src/Envoy.App/IShell.cs` | new | |
| `src/Envoy.App/Controls/AutoApply.cs` | new: attached `Ignore` | |
| `src/Envoy.App/Services/SettingsAutoApplier.cs` | new | |
| `src/Envoy.App/Pages/{WarRoom,Translation,ReadingChat,Overlay,Replying,Phrasebook,Hotkeys,Appearance,About}Page.xaml(.cs)` | new: XAML cut from `MainWindow.xaml` with handlers swapped for commands | |
| `src/Envoy.App/Pages/PageIds.cs` | new | |
| `src/Envoy.App/ViewModels/MainViewModel.cs` | adds ctor params, `ObservableObject` base, commands, test/phrase state, `CapturePreview`, `Dispose`; split into partials | |
| `src/Envoy.App/ViewModels/MainViewModel.{Translation,ReadingChat,Phrasebook,Actions}.cs` | new partials (SHOULD) | |
| `src/Envoy.App/ViewModels/{Option,OcrLanguageItem,ThemeOption,CapturePreviewViewModel}.cs` | moved / new | |
| `src/Envoy.App/Windows/MainWindow.xaml` | about 794 → about 90 lines: chrome + nav + 9 page hosts, `xmlns:p="clr-namespace:Envoy.App.Pages"`, `c:AutoApply.Ignore` on `Nav` | **yes (MainWindow\*)** |
| `src/Envoy.App/Windows/MainWindow.xaml.cs` | about 231 → about 80 lines. Removes all page handlers, `PhraseRow`, `FillPhrases`, `SetPreview`/`OnFrame`. Adds `SettingsAutoApplier`, `PageFor(string)`, `PageNames => PageIds.All`. Keeps `MainWindow(AppHost)` signature, `Navigate`, chrome handlers | **yes (MainWindow\*)** |
| `src/Envoy.App/App.xaml.cs` | `public partial class App : Application, IShell`; adds `public void ShowInfo(string message)` and explicit `void IShell.OpenUrl(string url) => OpenUrl(url);`. `HotkeyErrors`, `SelectRegion`, `ToggleOverlayEditMode`, `OpenCompose`, `ScanAndTranslate` and `ExitApp` already exist with matching signatures. No behaviour change | **yes** |
| `src/Envoy.App/SmokeTest.cs` | binding-error listener around the `MainWindow` block; per-page load and visibility assertions via `PageFor`; screenshot list becomes `PageIds.All`; `using Envoy.App.Pages;` | **yes** |
| `src/Envoy.App/AppHost.cs` | **no change** | yes (untouched) |
| `src/Envoy.Core/Settings/AppSettings.cs` | **no change** | yes (untouched) |
| `tools/typecheck/gen_stubs.py` | base class, full-name files, duplicate detection, `x:FieldModifier`, event-handler `nameof` checks | |
| `tools/typecheck/test_gen_stubs.py`, `tools/typecheck/fixtures/*.xaml` | new | |
| `tools/typecheck/typecheck.sh` | runs the unittest discovery before generation | |
| `docs/architecture.md`, `CLAUDE.md`, `CHANGELOG.md` | describe `Pages/`, `Mvvm/` and commands; the settings rule now points at Pages | |

Estimated size is about 1,300 changed lines. Most of it is XAML moved verbatim; new C# and Python come to about 600
lines, plus about 350 lines of tests.

## Risks & open questions

- **Binding breakage across the UserControl boundary.** `ElementName` bindings to `Nav*` must stay in `MainWindow`.
  Any `ElementName` *inside* a page may only target elements in that same page. Mitigation: REQ-UIF-19 makes CI fail
  on binding errors. Unknown: there may already be binding errors today, for example on `PrimaryInfo.Name` or
  provider sections. Mitigation: in that case the implementer fixes them if they are trivial and behaviour-neutral, or
  adds a documented, exact-message allowlist in `SmokeTest.cs` and records it in the PR. A blanket suppression is not
  allowed.
- **Data-binding tracing in release builds.** The `DataBindingSource` switch must be set explicitly, because without
  a debugger WPF does not trace by default. If `Refresh()` plus the switch level still produce no output on the
  single-file published exe, fall back to also listening with `SourceLevels.Warning`. Confirm on the first CI run by
  temporarily introducing a bad binding on one page and checking that the smoke test fails.
- **Pixel parity.** Moving the subtree into a `UserControl` adds one element to the visual tree, and that should not
  change layout or rendering. Font hinting and layout rounding are inherited, so the risk is low. The CI-runner capture
  preview content is non-deterministic and is excluded from the comparison. If screenshots differ by sub-pixel
  anti-aliasing only, that is accepted, and the PR notes it.
- **Auto-apply via `Inherits`.** Inheritance of `AutoApply.Ignore` flows through the logical tree. Controls inside
  `ComboBox` popups or `DataTemplate`s still inherit, and that matches the identity check, which only compared the
  outer control. Risk: a settings control accidentally placed under an ignored container stops auto-applying.
  AC-UIF-3 covers the cases we know of.
- **The `App.Current` cast in `MainWindow`.** In the smoke test `App.Current` is the real `App`, so the cast is safe.
  A future headless test harness would need another `IShell`; that is out of scope here.
- **Event-name heuristic in `gen_stubs.py`.** A handler on an event outside `EVENT_NAMES` is not checked. That is a
  false negative only: the Windows build still catches it. The list can grow as needed.
- **Open question:** should `AsyncRelayCommand` expose cancellation (`CancellationToken`) for later areas, for example
  translation-resilience timeouts? It is left out to stay minimal and can be added without breaking callers.
- **Open question:** should a palette-only colour lint for `Pages/*.xaml` live in `check_resources.py`?
  `AppearancePage` would inherit the literal swatch stroke `#40000000`, and Overlay/Region windows have similar
  literals. That is deferred to quality-security-ci so this area stays behaviour- and palette-neutral.
- **Merge order.** Every other area edits page XAML or `MainViewModel`. This area must merge first, and the others
  should rebase onto it and edit `Pages/<Page>.xaml` plus the matching `MainViewModel.<Page>.cs` partial.
