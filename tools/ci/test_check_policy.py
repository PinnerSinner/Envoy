"""Tests for check_policy.py. Run: python3 -m unittest discover -s tools/ci -p "test_*.py"."""
from __future__ import annotations

import contextlib
import io
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(HERE))

import check_policy  # noqa: E402

# The smallest tree that passes: the real build configuration, manifest, banned list and policy.
SEED_FILES = [
    "Directory.Build.props",
    "BannedSymbols.txt",
    ".editorconfig",
    "src/Envoy.Core/Envoy.Core.csproj",
    "tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj",
    "src/Envoy.App/Envoy.App.csproj",
    "src/Envoy.App/app.manifest",
    "tools/ci/policy.json",
]


class PolicyTreeTest(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        for rel in SEED_FILES:
            target = self.root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / rel, target)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def write(self, rel: str, text: str) -> None:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def edit(self, rel: str, old: str, new: str) -> None:
        path = self.root / rel
        text = path.read_text(encoding="utf-8")
        self.assertIn(old, text)
        path.write_text(text.replace(old, new), encoding="utf-8")

    def rules(self, policy: dict | None = None) -> list[tuple[str, str]]:
        return [(v.path, v.rule) for v in check_policy.check(self.root, policy)]

    def assertRule(self, rule: str, path: str | None = None) -> None:
        hits = [(p, r) for p, r in self.rules() if r == rule and (path is None or p == path)]
        self.assertTrue(hits, f"expected a {rule} violation{' in ' + path if path else ''}, got {self.rules()}")

    def assertNoRule(self, rule: str) -> None:
        self.assertNotIn(rule, [r for _, r in self.rules()])


class RepositoryTests(unittest.TestCase):
    def test_repository_passes(self) -> None:
        violations = check_policy.check(REPO)
        self.assertEqual([], [str(v) for v in violations])

    def test_main_exit_codes(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(0, check_policy.main(["--root", str(REPO)]))
        self.assertIn("passed", out.getvalue())


class SeedTreeTests(PolicyTreeTest):
    def test_seed_tree_passes(self) -> None:
        self.assertEqual([], self.rules())


class NativeTests(PolicyTreeTest):
    def test_denied_api_fails_even_if_allowlisted(self) -> None:
        policy = check_policy.load_policy(self.root)
        policy["native"]["allowed"].append("kernel32!ReadProcessMemory")
        self.write("src/Envoy.App/Interop/Bad.cs",
                   '[DllImport("kernel32.dll")]\nstatic extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);\n')
        rules = [r for _, r in self.rules(policy)]
        self.assertIn("native-denied", rules)
        self.assertNotIn("native-unlisted", rules)

    def test_denied_name_in_comment_fails(self) -> None:
        self.write("src/Envoy.Core/X.cs", "// We never call OpenProcess.\n")
        self.assertRule("native-denied", "src/Envoy.Core/X.cs")

    def test_managed_dynamic_loading_is_denied(self) -> None:
        self.write("src/Envoy.App/A.cs", 'var h = NativeLibrary.Load("kernel32.dll");\n')
        self.write("src/Envoy.App/B.cs", "var f = Marshal.GetDelegateForFunctionPointer<Fn>(p);\n")
        self.assertRule("native-denied", "src/Envoy.App/A.cs")
        self.assertRule("native-denied", "src/Envoy.App/B.cs")

    def test_unlisted_pinvoke_fails(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs",
                   '[LibraryImport("user32.dll")]\n[return: MarshalAs(UnmanagedType.Bool)]\n'
                   "public static partial bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);\n")
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

    def test_entrypoint_is_used_for_key(self) -> None:
        self.write("src/Envoy.App/Interop/Ok.cs",
                   '[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]\n'
                   "public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);\n")
        self.assertNoRule("native-unlisted")

    def test_method_name_is_used_without_entrypoint(self) -> None:
        self.write("src/Envoy.App/Interop/Ok.cs",
                   '[DllImport("User32.DLL", SetLastError = true)]\npublic static extern uint SendInput(uint n, INPUT[] p, int cb);\n')
        self.assertNoRule("native-unlisted")


    def test_unsafe_partial_is_not_keyed_by_a_later_declaration(self) -> None:
        # The unlisted import must not borrow the name of the allowed one that follows it.
        self.write("src/Envoy.App/Interop/Extra.cs",
                   '[LibraryImport("user32.dll")]\n'
                   "internal static unsafe partial bool PostMessageW(nint h, uint m, nint w, nint l);\n\n"
                   '[LibraryImport("user32.dll")]\npublic static partial nint GetForegroundWindow();\n')
        self.assertEqual(["native-unlisted"], [r for _, r in self.rules()])

    def test_modifiers_in_any_order_resolve(self) -> None:
        self.write("src/Envoy.App/Interop/Ok.cs",
                   '[DllImport("user32.dll")]\nextern static unsafe internal uint SendInput(uint n, void* p, int cb);\n'
                   '[LibraryImport("gdi32.dll")]\n// a comment\n[return: MarshalAs(UnmanagedType.Bool)]\n'
                   "private static unsafe partial bool DeleteDC(nint hdc);\n")
        self.assertEqual([], self.rules())

    def test_constant_library_name_fails(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs",
                   'const string Lib = "user32.dll";\n[DllImport(Lib)]\nstatic extern uint SendInput(uint n, INPUT[] p, int cb);\n')
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

    def test_constant_entry_point_fails(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs",
                   '[LibraryImport("user32.dll", EntryPoint = Name)]\npublic static partial nint GetForegroundWindow();\n')
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

    def test_combined_attribute_list_fails(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs",
                   '[SupportedOSPlatform("windows"), DllImport("kernel32")]\nstatic extern bool VirtualFree(nint a, nint s, uint t);\n')
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

    def test_qualified_attribute_name_is_checked(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs",
                   '[System.Runtime.InteropServices.DllImport("kernel32")]\nstatic extern bool VirtualFree(nint a, nint s, uint t);\n')
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

    def test_qualified_allowed_attribute_passes(self) -> None:
        self.write("src/Envoy.App/Interop/Ok.cs",
                   '[global::System.Runtime.InteropServices.LibraryImportAttribute("user32")]\n'
                   "public static partial nint GetForegroundWindow();\n")
        self.assertNoRule("native-unlisted")

    def test_import_attribute_without_a_method_fails(self) -> None:
        self.write("src/Envoy.App/Interop/Extra.cs", '[DllImport("user32.dll")]\nstatic int x;\n')
        self.assertRule("native-unlisted", "src/Envoy.App/Interop/Extra.cs")

class NetworkAndHostTests(PolicyTreeTest):
    def test_network_type_outside_allowed_paths_fails(self) -> None:
        self.write("src/Envoy.Core/Chat/X.cs", "class X { HttpClient _http = new(); }\n")
        self.assertRule("network-location", "src/Envoy.Core/Chat/X.cs")

    def test_network_exception_type_is_not_a_network_type(self) -> None:
        self.write("src/Envoy.Core/Chat/X.cs", "class X { void F() { try { } catch (HttpRequestException) { } } }\n")
        self.assertNoRule("network-location")

    def test_network_type_inside_allowed_paths_passes(self) -> None:
        self.write("src/Envoy.Core/Translation/X.cs", "class X { HttpClient _http = new(); }\n")
        self.write("src/Envoy.App/AppHost.cs", "class AppHost { HttpClient _http = new(); }\n")
        self.assertNoRule("network-location")

    def test_unknown_host_fails_and_schema_hosts_ignored(self) -> None:
        self.write("src/Envoy.Core/X.cs", 'const string U = "https://telemetry.example/collect";\n')
        self.write("src/Envoy.App/Pages/P.xaml",
                   '<UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"/>\n')
        self.assertEqual([("src/Envoy.Core/X.cs", "host")], [x for x in self.rules() if x[1] == "host"])

    def test_known_host_passes(self) -> None:
        self.write("src/Envoy.Core/X.cs", 'const string U = "https://github.com/PinnerSinner/Envoy";\n')
        self.assertNoRule("host")


class ManifestTests(PolicyTreeTest):
    def test_manifest_requires_asInvoker(self) -> None:
        self.edit("src/Envoy.App/app.manifest", 'level="asInvoker"', 'level="requireAdministrator"')
        self.assertRule("manifest")

    def test_manifest_rejects_uiaccess(self) -> None:
        self.edit("src/Envoy.App/app.manifest", 'uiAccess="false"', 'uiAccess="true"')
        self.assertRule("manifest")

    def test_manifest_rejects_single_quoted_uiaccess(self) -> None:
        self.edit("src/Envoy.App/app.manifest", 'uiAccess="false"', "uiAccess='true'")
        self.assertRule("manifest")

    def test_manifest_ignores_asinvoker_in_a_comment(self) -> None:
        self.edit("src/Envoy.App/app.manifest", '<requestedExecutionLevel level="asInvoker"',
                  '<!-- level="asInvoker" --><requestedExecutionLevel level="requireAdministrator"')
        self.assertRule("manifest")

    def test_manifest_rejects_two_execution_levels(self) -> None:
        self.edit("src/Envoy.App/app.manifest", '<requestedExecutionLevel level="asInvoker" uiAccess="false" />',
                  '<requestedExecutionLevel level="asInvoker" uiAccess="false" />\n'
                  '        <requestedExecutionLevel level="requireAdministrator" />')
        self.assertRule("manifest")

    def test_manifest_without_uiaccess_passes(self) -> None:
        self.edit("src/Envoy.App/app.manifest", ' uiAccess="false"', "")
        self.assertNoRule("manifest")

    def test_invalid_manifest_xml_fails(self) -> None:
        self.edit("src/Envoy.App/app.manifest", "</assembly>", "")
        self.assertRule("manifest")

    def test_missing_manifest_fails(self) -> None:
        (self.root / "src/Envoy.App/app.manifest").unlink()
        self.assertRule("manifest")


class LauncherAndPragmaTests(PolicyTreeTest):
    LAUNCHER = "src/Envoy.App/Security/ShellLauncher.cs"

    def test_process_start_outside_launcher_fails(self) -> None:
        self.write("src/Envoy.App/Pages/X.xaml.cs", 'class X { void F() => Process.Start("x"); }\n')
        self.assertRule("process-launch", "src/Envoy.App/Pages/X.xaml.cs")

    def test_process_start_in_launcher_passes(self) -> None:
        self.write(self.LAUNCHER, '#pragma warning disable RS0030 // sanctioned\nclass L { void F() => Process.Start("x"); }\n')
        self.assertEqual([], self.rules())

    def test_start_info_and_shell_execute_outside_launcher_fail(self) -> None:
        self.write("src/Envoy.App/A.cs", "var i = new ProcessStartInfo();\n")
        self.write("src/Envoy.App/B.cs", "x.UseShellExecute = true;\n")
        self.assertRule("process-launch", "src/Envoy.App/A.cs")
        self.assertRule("process-launch", "src/Envoy.App/B.cs")

    def test_pragma_only_in_launcher(self) -> None:
        self.write("src/Envoy.Core/X.cs", "#pragma warning disable CA1305\n")
        self.assertRule("pragma", "src/Envoy.Core/X.cs")

    def test_pragma_in_tests_fails(self) -> None:
        self.write("tests/Envoy.Core.Tests/X.cs", "#pragma warning disable CS0168\n")
        self.assertRule("pragma", "tests/Envoy.Core.Tests/X.cs")

    def test_second_rs0030_pragma_fails(self) -> None:
        self.write(self.LAUNCHER, "#pragma warning disable RS0030\nclass L { }\n#pragma warning disable RS0030\n")
        self.assertRule("pragma", self.LAUNCHER)

    def test_rs0030_pragma_outside_launcher_fails(self) -> None:
        self.write("src/Envoy.App/X.cs", "#pragma warning disable RS0030\n")
        self.assertRule("pragma", "src/Envoy.App/X.cs")

    def test_other_ids_in_launcher_pragma_fail(self) -> None:
        self.write(self.LAUNCHER, "#pragma warning disable RS0030, CA1305\n")
        self.assertRule("pragma", self.LAUNCHER)

    def test_suppress_message_needs_justification(self) -> None:
        self.write("src/Envoy.Core/X.cs", '[SuppressMessage("x","y")]\nclass X { }\n')
        self.assertRule("pragma", "src/Envoy.Core/X.cs")

    def test_suppress_message_needs_non_empty_justification(self) -> None:
        self.write("src/Envoy.Core/X.cs", '[SuppressMessage("x","y", Justification = "")]\nclass X { }\n')
        self.write("src/Envoy.Core/Y.cs", '[SuppressMessage("x","y", Justification = "  ")]\nclass Y { }\n')
        self.assertRule("pragma", "src/Envoy.Core/X.cs")
        self.assertRule("pragma", "src/Envoy.Core/Y.cs")

    def test_multiline_suppress_message_with_justification_passes(self) -> None:
        self.write("src/Envoy.Core/X.cs",
                   '[SuppressMessage("Design", "CA1001:Types (that own) fields",\n    Justification = "Reason.")]\nclass X { }\n')
        self.assertNoRule("pragma")

    def test_suppressing_rs0030_fails(self) -> None:
        self.write("src/Envoy.Core/X.cs", '[SuppressMessage("ApiDesign", "RS0030", Justification = "no")]\nclass X { }\n')
        self.assertRule("pragma", "src/Envoy.Core/X.cs")


class BannedSymbolsTests(PolicyTreeTest):
    def test_banned_symbols_required_ids(self) -> None:
        self.edit("BannedSymbols.txt", "T:System.Diagnostics.ProcessStartInfo;", "T:System.Diagnostics.Other;")
        self.assertRule("banned-symbols")

    def test_missing_banned_symbols_fails(self) -> None:
        (self.root / "BannedSymbols.txt").unlink()
        self.assertRule("banned-symbols")


class BuildConfigTests(PolicyTreeTest):
    def test_build_config_requires_nullable_errors(self) -> None:
        self.edit("src/Envoy.Core/Envoy.Core.csproj", "$(WarningsAsErrors);nullable", "$(WarningsAsErrors)")
        self.assertRule("build-config", "src/Envoy.Core/Envoy.Core.csproj")

    def test_build_config_rejects_twae_false_outside_app(self) -> None:
        self.edit("src/Envoy.Core/Envoy.Core.csproj", "</PropertyGroup>",
                  "<TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>")
        self.assertRule("build-config", "src/Envoy.Core/Envoy.Core.csproj")

    def test_build_config_allows_twae_false_in_app(self) -> None:
        self.assertIn("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
                      (self.root / "src/Envoy.App/Envoy.App.csproj").read_text(encoding="utf-8"))
        self.assertNoRule("build-config")

    def test_build_config_rejects_rs0030_nowarn(self) -> None:
        self.edit("src/Envoy.Core/Envoy.Core.csproj", "</PropertyGroup>", "<NoWarn>$(NoWarn);RS0030</NoWarn></PropertyGroup>")
        self.assertRule("build-config", "src/Envoy.Core/Envoy.Core.csproj")

    def test_build_config_rejects_lowered_rs0030_severity(self) -> None:
        self.write("src/Envoy.Core/.editorconfig", "[*.cs]\ndotnet_diagnostic.RS0030.severity = none\n")
        self.assertRule("build-config", "src/Envoy.Core/.editorconfig")

    def test_build_config_requires_props_settings(self) -> None:
        self.edit("Directory.Build.props", "<Nullable>enable</Nullable>", "")
        self.assertRule("build-config", "Directory.Build.props")

    def test_build_config_requires_ci_warnings_as_errors(self) -> None:
        self.edit("Directory.Build.props", "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", "")
        self.assertRule("build-config", "Directory.Build.props")

    def test_build_config_requires_banned_api_analyzer(self) -> None:
        self.edit("Directory.Build.props", "Microsoft.CodeAnalysis.BannedApiAnalyzers", "Something.Else")
        self.assertRule("build-config", "Directory.Build.props")


    def test_build_config_rejects_analyzers_switched_off(self) -> None:
        for name in ("RunAnalyzers", "RunAnalyzersDuringBuild", "EnableNETAnalyzers"):
            with self.subTest(name=name):
                rel = f"src/{name}/{name}.csproj"
                self.write(rel, f"<Project><PropertyGroup><{name}>false</{name}></PropertyGroup></Project>\n")
                self.assertRule("build-config", rel)

    def test_build_config_rejects_analyzers_off_in_app(self) -> None:
        self.edit("src/Envoy.App/Envoy.App.csproj", "</PropertyGroup>", "<RunAnalyzers>False</RunAnalyzers></PropertyGroup>")
        self.assertRule("build-config", "src/Envoy.App/Envoy.App.csproj")

    def test_build_config_rejects_removed_banned_api_analyzer(self) -> None:
        self.edit("src/Envoy.Core/Envoy.Core.csproj", "</Project>",
                  '<ItemGroup><PackageReference Remove="Microsoft.CodeAnalysis.BannedApiAnalyzers" /></ItemGroup></Project>')
        self.assertRule("build-config", "src/Envoy.Core/Envoy.Core.csproj")

    def test_build_config_rejects_removed_banned_symbols(self) -> None:
        self.edit("src/Envoy.Core/Envoy.Core.csproj", "</Project>",
                  '<ItemGroup><AdditionalFiles Remove="$(MSBuildThisFileDirectory)../../BannedSymbols.txt" /></ItemGroup></Project>')
        self.assertRule("build-config", "src/Envoy.Core/Envoy.Core.csproj")

    def test_build_config_rejects_nested_directory_build_props(self) -> None:
        self.write("src/Directory.Build.props", "<Project></Project>\n")
        self.assertRule("build-config", "src/Directory.Build.props")

if __name__ == "__main__":
    unittest.main()
