"""Tests for check_ui_rules.py (REQ-UIF-17, specs 02, 05, 06 and 07a): a clean tree passes and each rule R1-R12, UPD1 and UPD2
reports its own violation.
The mini trees are built in a temp directory from check_ui_rules.REQUIRED_FILES, so they track the real file list."""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import check_ui_rules  # noqa: E402

PAGE_XAML = """<UserControl x:Class="Envoy.App.Pages.{name}"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:Envoy.App.Controls">
    <ScrollViewer Padding="32,24">
        <StackPanel>
            <Button Content="Go" Command="{{Binding GoCommand}}" />
            <TextBox c:AutoApply.Ignore="True" Text="{{Binding TestInput}}" />
            <ItemsControl ItemsSource="{{Binding Items}}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Button Command="{{Binding DataContext.InstallCommand, RelativeSource={{RelativeSource AncestorType=UserControl}}}}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </ScrollViewer>
</UserControl>
"""

PAGE_CS = """namespace Envoy.App.Pages;

public partial class {name} : System.Windows.Controls.UserControl
{{
    public {name}()
    {{
        InitializeComponent();
        IsVisibleChanged += (_, _) => {{ if (DataContext is object vm) _ = vm; }};
    }}
}}
"""

PROGRAM_OK = ("internal static class Program { static void Main() { VelopackApp.Build()\n"
              "    .SetAutoApplyOnStartup(false)\n    .Run(); } }\n")

MAIN_WINDOW = """<Window x:Class="Envoy.App.Windows.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid><ContentControl /></Grid>
</Window>
"""


class CheckUiRulesTests(unittest.TestCase):
    def setUp(self):
        self.base = tempfile.mkdtemp(prefix="envoy-ui-rules-")
        self.root = os.path.join(self.base, "src", "Envoy.App")
        for rel in check_ui_rules.REQUIRED_FILES:
            name = os.path.basename(rel).split(".")[0]
            if rel.endswith(".xaml"):
                content = PAGE_XAML.format(name=name)
            elif rel.startswith("Pages/") and rel.endswith(".xaml.cs"):
                content = PAGE_CS.format(name=name)
            else:
                content = "namespace Envoy.App;\n// MessageBox in a comment is fine.\n"
            self.write(rel, content)
        self.write("Windows/MainWindow.xaml", MAIN_WINDOW)
        self.write("Program.cs", PROGRAM_OK)
        self.write("Services/DialogService.cs", "class DialogService { void X() => System.Windows.MessageBox.Show(\"\"); }\n")
        self.write("ViewModels/MainViewModel.cs", "class MainViewModel { IShell? _shell; }\n")

    def tearDown(self):
        shutil.rmtree(self.base, ignore_errors=True)

    def write(self, rel, content):
        path = os.path.join(self.root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)

    def append(self, rel, content):
        with open(os.path.join(self.root, *rel.split("/")), "a", encoding="utf-8") as f:
            f.write(content)

    def assertOnly(self, rule, path_fragment):
        problems = check_ui_rules.check(self.root)
        self.assertTrue(problems, f"expected a {rule} violation")
        for p in problems:
            self.assertIn(f": UIF-{rule} ", p)
            self.assertIn(path_fragment, p)
            self.assertRegex(p, r"^[^:]+:\d+: UIF-R\d+ ")

    def test_clean_tree_passes(self):
        self.assertEqual([], check_ui_rules.check(self.root))
        result = subprocess.run([sys.executable, os.path.join(HERE, "check_ui_rules.py"), self.root], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout)

    def test_datacontext_in_relativesource_path_is_fine(self):
        # The clean page template already binds DataContext.InstallCommand through RelativeSource.
        self.assertEqual([], [p for p in check_ui_rules.check(self.root) if "UIF-R3" in p])

    def test_r1_event_handler_in_page(self):
        self.write("Pages/OverlayPage.xaml", PAGE_XAML.format(name="OverlayPage").replace('Content="Go"', 'Content="Go" Click="OnGo"'))
        self.assertOnly("R1", "Pages/OverlayPage.xaml:7")

    def test_r1_attached_event_in_page(self):
        self.write("Pages/AboutPage.xaml", PAGE_XAML.format(name="AboutPage").replace("<StackPanel>", '<StackPanel ButtonBase.Click="OnAny">'))
        self.assertOnly("R1", "Pages/AboutPage.xaml")

    def test_r2_routed_event_args_in_page_code(self):
        self.append("Pages/HotkeysPage.xaml.cs", "partial class HotkeysPage { void OnX(object s, System.Windows.RoutedEventArgs e) { } }\n")
        self.assertOnly("R2", "Pages/HotkeysPage.xaml.cs")

    def test_r2_datacontext_assignment_in_page_code(self):
        self.append("Pages/HotkeysPage.xaml.cs", "partial class HotkeysPage { void Set() { DataContext = new object(); } }\n")
        self.assertOnly("R2", "Pages/HotkeysPage.xaml.cs")

    def test_r3_datacontext_attribute_in_page(self):
        self.write("Pages/ReplyingPage.xaml", PAGE_XAML.format(name="ReplyingPage").replace("<StackPanel>", '<StackPanel DataContext="{Binding Settings}">'))
        self.assertOnly("R3", "Pages/ReplyingPage.xaml:6")

    def test_r3_datacontext_property_element_in_page(self):
        self.write("Pages/ReplyingPage.xaml", PAGE_XAML.format(name="ReplyingPage").replace(
            "<ScrollViewer Padding=\"32,24\">", "<UserControl.DataContext><Grid /></UserControl.DataContext>\n    <ScrollViewer Padding=\"32,24\">"))
        self.assertOnly("R3", "Pages/ReplyingPage.xaml")

    def test_r4_banned_api_in_view_model(self):
        for snippet in ("var a = App.Current;", "Process.Start(\"x\");", "new ProcessStartInfo(\"x\");", "MessageBox.Show(\"x\");"):
            with self.subTest(snippet=snippet):
                self.write("ViewModels/MainViewModel.Actions.cs", f"class A {{ void X() {{ {snippet} }} }}\n")
                problems = [p for p in check_ui_rules.check(self.root) if "UIF-R4" in p]
                self.assertEqual(1, len(problems), problems)
                self.assertIn("ViewModels/MainViewModel.Actions.cs:1:", problems[0])

    def test_r5_messagebox_outside_dialog_service(self):
        self.write("Windows/ComposeWindow.xaml.cs", "class C { void X() => MessageBox.Show(\"x\"); }\n")
        self.assertOnly("R5", "Windows/ComposeWindow.xaml.cs:1")

    def test_r6_scrollviewer_in_main_window(self):
        self.write("Windows/MainWindow.xaml", MAIN_WINDOW.replace("<ContentControl />", "<ScrollViewer />"))
        self.assertOnly("R6", "Windows/MainWindow.xaml:4")

    def test_r7_missing_file(self):
        os.remove(os.path.join(self.root, "Pages", "PhrasebookPage.xaml.cs"))
        self.assertOnly("R7", "Pages/PhrasebookPage.xaml.cs")

    def test_r7_missing_dialog_service(self):
        os.remove(os.path.join(self.root, "Services", "DialogService.cs"))
        self.assertOnly("R7", "Services/DialogService.cs")

    def write_core(self, rel, content):
        path = os.path.join(self.base, "src", "Envoy.Core", *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)

    def test_r8_appdata_outside_settings_store(self):
        self.write_core("Settings/SettingsStore.cs", "class S { string P => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }\n")
        self.write_core("Chat/Other.cs", "// %APPDATA% in a comment is fine.\nclass O { }\n")
        self.assertEqual([], check_ui_rules.check(self.root))
        self.write("AppHost.cs", "class H { string D => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }\n")
        self.assertOnly("R8", "AppHost.cs:1")

    def test_r8_appdata_in_core(self):
        self.write_core("Chat/TranscriptWriter.cs", "class T { string D => Environment.ExpandEnvironmentVariables(\"%APPDATA%\"); }\n")
        self.assertOnly("R8", "Envoy.Core/Chat/TranscriptWriter.cs:1")

    def test_r9_async_discard(self):
        self.write("Services/SpeechService.cs", "class S { void X() { _ = SpeakNextAsync(); } }\n")
        self.assertOnly("R9", "Services/SpeechService.cs:1")
        self.write("Services/SpeechService.cs", "class S { void X() { _ = pipeline.ProcessAsync(lines, ct).ContinueWith(t => 1); } }\n")
        self.assertOnly("R9", "Services/SpeechService.cs:1")

    def test_r9_forget_and_sync_discards_are_fine(self):
        self.write("Services/SpeechService.cs",
                   "class S { void X() { SpeakNextAsync().Forget(log, \"speech\"); _ = ShellLauncher.TryOpen(u, d); if (a == _ ) { } } }\n")
        self.assertEqual([], check_ui_rules.check(self.root))

    def test_r10_dialog_in_compose_window(self):
        self.write("Windows/ComposeWindow.xaml.cs", "class C { void X() => DialogService.ShowInfo(null, \"a\", \"b\"); }\n")
        self.assertOnly("R10", "Windows/ComposeWindow.xaml.cs:1")

    def test_r10_second_show_warning_in_app(self):
        self.write("App.xaml.cs", "class A {\n void X() => DialogService.ShowWarning(null, \"a\", \"b\");\n}\n")
        self.assertEqual([], check_ui_rules.check(self.root))
        self.append("App.xaml.cs", "class B { void Y() => DialogService.ShowWarning(null, \"c\", \"d\"); }\n")
        self.assertOnly("R10", "App.xaml.cs:4")

    WATCHER_OK = """using Envoy.Core.Settings;
// pipeline.ProcessAsync(lines, ct) in a comment is fine, and so is a comment about AppSettings.
namespace Envoy.App.Services; class ChatWatcher
{
    void Tick(System.Func<CaptureOptions> options)
    {
        var o = options();
        _pipeline.ProcessAsync(lines, CancellationToken.None).Forget(_log, "pipeline");
    }
}
"""

    WIZARD_OK = """namespace Envoy.App.ViewModels; class SetupWizardViewModel
{
    // SaveNow and new AppSettings() in a comment are fine.
    void Edit() { Host.Settings.MyLanguage = "en"; Host.ScheduleSave(); }
}
"""

    def test_r12_wizard_edits_through_host_settings_are_fine(self):
        self.write("ViewModels/SetupWizardViewModel.cs", self.WIZARD_OK)
        self.assertEqual([], check_ui_rules.check(self.root))

    def test_r12_wizard_new_settings_or_save_now(self):
        self.write("ViewModels/SetupWizardViewModel.cs", self.WIZARD_OK.replace("Host.ScheduleSave()", "Host.SaveNow()"))
        self.assertOnly("R12", "ViewModels/SetupWizardViewModel.cs:4")
        self.write("ViewModels/SetupWizardViewModel.cs", self.WIZARD_OK.replace("Host.ScheduleSave();", "var s = new AppSettings();"))
        self.assertOnly("R12", "ViewModels/SetupWizardViewModel.cs:4")

    def test_r11_watcher_token_and_snapshot_are_fine(self):
        self.write("Services/ChatWatcher.cs", self.WATCHER_OK)
        self.assertEqual([], check_ui_rules.check(self.root))

    def test_r11_watcher_passes_loop_token(self):
        self.write("Services/ChatWatcher.cs", self.WATCHER_OK.replace("CancellationToken.None", "ct"))
        self.assertOnly("R11", "Services/ChatWatcher.cs:8")
        self.write("Services/ChatWatcher.cs", self.WATCHER_OK.replace("(lines, CancellationToken.None)", "(lines)"))
        self.assertOnly("R11", "Services/ChatWatcher.cs:8")

    def test_r11_watcher_reads_settings(self):
        for snippet in ("var s = host.Settings.Capture;", "System.Func<AppSettings> settings = null;"):
            with self.subTest(snippet=snippet):
                self.write("Services/ChatWatcher.cs", self.WATCHER_OK.replace("var o = options();", snippet))
                self.assertOnly("R11", "Services/ChatWatcher.cs:7")


    # ---- spec 06 ----

    PROGRAM_OK = PROGRAM_OK

    def assertUpd(self, rule, path_fragment):
        problems = check_ui_rules.check(self.root)
        self.assertTrue(problems, f"expected a {rule} violation")
        for p in problems:
            self.assertRegex(p, rf"^[^:]+:\d+: {rule} ")
            self.assertIn(path_fragment, p)

    def test_upd_velopack_in_its_places_is_fine(self):
        self.write("Program.cs", self.PROGRAM_OK)
        self.write("Services/VelopackUpdateBackend.cs", "class B { object M() => new UpdateManager(source, options); }\n")
        self.write_core("Updates/Notes.cs", "// ApplyUpdatesAndRestart in a comment is fine.\nclass N { }\n")
        self.assertEqual([], check_ui_rules.check(self.root))

    def test_upd1_update_manager_elsewhere(self):
        self.write("AppHost.cs", "class H { object M() => new Velopack.UpdateManager(source); }\n")
        self.assertUpd("UPD1", "AppHost.cs")

    def test_upd1_target_typed_or_subclassed_update_manager(self):
        self.write("AppHost.cs", "class H { void M() { UpdateManager mgr = new(source, options); } }\n")
        self.assertUpd("UPD1", "AppHost.cs")
        self.write("AppHost.cs", "class H { void M() { Velopack.UpdateManager? mgr = new (source); } }\n")
        self.assertUpd("UPD1", "AppHost.cs")
        self.write("AppHost.cs", "sealed class MyManager(UpdateSource s) : UpdateManager(s) { }\n")
        self.assertUpd("UPD1", "AppHost.cs")
        self.write("AppHost.cs", "class H { UpdateManager? _m; object M(bool b) => b ? null : UpdateManager.Default; }\n")
        self.assertEqual([], check_ui_rules.check(self.root))

    def test_upd1_flow_source_or_apply_and_restart(self):
        self.write("Services/VelopackUpdateBackend.cs", "class B { void M() { mgr.ApplyUpdatesAndRestart(x); } }\n")
        self.assertUpd("UPD1", "VelopackUpdateBackend.cs")
        self.write("Services/VelopackUpdateBackend.cs", "class B { }\n")
        self.write_core("Updates/Flow.cs", "class F { object S => new VelopackFlowSource(); }\n")
        self.assertUpd("UPD1", "Flow.cs")

    def test_upd2_velopack_app_outside_program(self):
        self.write("Program.cs", self.PROGRAM_OK)
        self.write("App.xaml.cs", "class A { void M() { VelopackApp.Build().Run(); } }\n")
        self.assertUpd("UPD2", "App.xaml.cs")

    def test_upd2_program_without_auto_apply_off(self):
        self.write("Program.cs", "class P { static void Main() { VelopackApp.Build()\n    .SetAutoApplyOnStartup(true).Run(); } }\n")
        self.assertUpd("UPD2", "Program.cs")
        self.write("Program.cs", "class P { static void Main() { VelopackApp.Build()\n    // .SetAutoApplyOnStartup(false)\n    .Run(); } }\n")
        self.assertUpd("UPD2", "Program.cs")

    def test_upd2_program_must_run_velopack(self):
        self.write("Program.cs", "class P { static void Main() { new App().Run(); } }\n")
        self.assertUpd("UPD2", "Program.cs")
        self.write("Program.cs", "class P { static void Main() {\n    // VelopackApp.Build().SetAutoApplyOnStartup(false).Run();\n    new App().Run(); } }\n")
        self.assertUpd("UPD2", "Program.cs")
        self.write("Program.cs", "class P { static void Main() { var b = VelopackApp.Build().SetAutoApplyOnStartup(false); } }\n")
        self.assertUpd("UPD2", "Program.cs")
        os.remove(os.path.join(self.root, "Program.cs"))
        self.assertUpd("UPD2", "Program.cs")


if __name__ == "__main__":
    unittest.main()
