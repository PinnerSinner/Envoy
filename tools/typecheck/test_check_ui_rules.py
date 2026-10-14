"""Tests for check_ui_rules.py (REQ-UIF-17): a clean tree passes and each rule R1-R7 reports its own violation.
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

MAIN_WINDOW = """<Window x:Class="Envoy.App.Windows.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid><ContentControl /></Grid>
</Window>
"""


class CheckUiRulesTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="envoy-ui-rules-")
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
        self.write("Services/DialogService.cs", "class DialogService { void X() => System.Windows.MessageBox.Show(\"\"); }\n")
        self.write("ViewModels/MainViewModel.cs", "class MainViewModel { IShell? _shell; }\n")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

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
            self.assertRegex(p, r"^[^:]+:\d+: UIF-R\d ")

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


if __name__ == "__main__":
    unittest.main()
