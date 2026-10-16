"""Mechanical checks for the UI-structure rules of specs 01 and 02 (stdlib only).

Usage: check_ui_rules.py <src/Envoy.App dir>. Prints `path:line: UIF-Rn message` (or `UPDn message`) per violation and
exits 1 if any.

R1  Pages/*.xaml has no event-handler attribute (the gen_stubs matcher)               REQ-UIF-7
R2  Pages/*.xaml.cs contains no RoutedEventArgs and no `DataContext =`                REQ-UIF-7, REQ-UIF-2
R3  Pages/*.xaml sets no DataContext (attribute or property element)                  REQ-UIF-2
R4  ViewModels/*.cs contain no App.Current, Process.Start, ProcessStartInfo, MessageBox  REQ-UIF-9
R5  MessageBox.Show appears only in Services/DialogService.cs                         REQ-UIF-9
R6  Windows/MainWindow.xaml contains no ScrollViewer                                  REQ-UIF-1
R7  The page, view-model and service files of spec 01 exist                           REQ-UIF-1, REQ-UIF-21
R8  SpecialFolder.ApplicationData / APPDATA only in Envoy.Core/Settings/SettingsStore.cs  REQ-REL-44
R9  no `_ = <expr>Async(` discard in Envoy.App (use `.Forget(log, what)`)               REQ-REL-16
R10 Windows/ComposeWindow.xaml.cs uses no DialogService; App.xaml.cs calls
    DialogService.ShowWarning at most once (the crash-loop message)                     REQ-REL-19
R11 Services/ChatWatcher.cs passes only CancellationToken.None to ProcessAsync( and never
    reads AppSettings or `.Settings` outside its usings (it reads the CaptureOptions snapshot)  REQ-CAP-42
UPD1 `new UpdateManager(` only in Services/VelopackUpdateBackend.cs; VelopackFlowSource and
    ApplyUpdatesAndRestart nowhere (also scanned in Envoy.Core)                          REQ-DIST-14, 12
UPD2 `VelopackApp.Build()` only in Program.cs, which calls SetAutoApplyOnStartup(false)   REQ-DIST-10, 12

R8 also scans the sibling Envoy.Core folder (`<app dir>/../Envoy.Core`) when it exists.
"""
import glob
import os
import re
import sys
import xml.parsers.expat

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_stubs import is_event_handler  # noqa: E402

PAGES = ["WarRoomPage", "TranslationPage", "ReadingChatPage", "OverlayPage", "ReplyingPage", "PhrasebookPage",
         "HotkeysPage", "AppearancePage", "AboutPage"]

REQUIRED_FILES = (
    [f"Pages/{p}.xaml" for p in PAGES]
    + [f"Pages/{p}.xaml.cs" for p in PAGES]
    + [
        "Pages/PageIds.cs",
        "ViewModels/MainViewModel.Translation.cs",
        "ViewModels/MainViewModel.ReadingChat.cs",
        "ViewModels/MainViewModel.Phrasebook.cs",
        "ViewModels/MainViewModel.Actions.cs",
        "ViewModels/CapturePreviewViewModel.cs",
        "Services/DialogService.cs",
    ]
)

WATCHER = "Services/ChatWatcher.cs"
VELOPACK_BACKEND = "Services/VelopackUpdateBackend.cs"
PROGRAM = "Program.cs"
NEW_UPDATE_MANAGER = re.compile(r"\bnew\s+(?:Velopack\s*\.\s*)?UpdateManager\s*\(")
VELOPACK_BANNED = re.compile(r"\bVelopackFlowSource\b|\bApplyUpdatesAndRestart\b")
VELOPACK_BUILD = re.compile(r"\bVelopackApp\s*\.\s*Build\s*\(")
AUTO_APPLY_OFF = re.compile(r"\.\s*SetAutoApplyOnStartup\s*\(\s*false\s*\)")
PROCESS_ASYNC = re.compile(r"\bProcessAsync\s*\(")
WATCHER_SETTINGS = re.compile(r"\bAppSettings\b|\.Settings\b")
APPDATA = re.compile(r"\bSpecialFolder\.ApplicationData\b|APPDATA")
APPDATA_ALLOWED = "Envoy.Core/Settings/SettingsStore.cs"
ASYNC_DISCARD = re.compile(r"(?<![\w.])_\s*=\s*(?!=)[^;]*?\w+Async\s*\(")
VIEWMODEL_BANNED = re.compile(r"\bApp\.Current\b|\bProcess\.Start\b|\bProcessStartInfo\b|\bMessageBox\b")
DATACONTEXT_ASSIGN = re.compile(r"\bDataContext\s*=(?!=)")


def _rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


def _read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def _code_lines(text):
    """(line number, line) pairs, skipping whole-line comments."""
    for n, line in enumerate(text.splitlines(), 1):
        stripped = line.lstrip()
        if stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("/*"):
            continue
        yield n, line


def _xaml_elements(path):
    """Yields (line, element local name, attributes) using expat so violations carry line numbers."""
    found = []
    parser = xml.parsers.expat.ParserCreate(namespace_separator=" ")

    def start(name, attrs):
        local = name.rsplit(" ", 1)[-1]
        found.append((parser.CurrentLineNumber, local, attrs))

    parser.StartElementHandler = start
    with open(path, "rb") as f:
        parser.ParseFile(f)
    return found


def check(root):
    """Returns a list of violation strings for the app source tree at root."""
    problems = []

    def report(path, line, rule, message):
        # Spec 06's rules carry their own prefix (UPD1, UPD2); the others are UIF-Rn.
        prefix = "" if rule.startswith("UPD") else "UIF-"
        problems.append(f"{_rel(root, path)}:{line}: {prefix}{rule} {message}")

    pages = os.path.join(root, "Pages")
    for path in sorted(glob.glob(os.path.join(pages, "*.xaml"))):
        try:
            elements = _xaml_elements(path)
        except xml.parsers.expat.ExpatError as e:
            report(path, e.lineno, "R1", f"XAML does not parse: {e}")
            continue
        for line, local, attrs in elements:
            for attr, value in attrs.items():
                if is_event_handler(local, attr, value):
                    report(path, line, "R1", f"event handler {attr}=\"{value}\" in a page; bind a MainViewModel command instead")
                if attr == "DataContext":
                    report(path, line, "R3", "pages must not set DataContext; they inherit MainWindow's MainViewModel")
            if local.endswith(".DataContext"):
                report(path, line, "R3", "pages must not set DataContext; they inherit MainWindow's MainViewModel")

    for path in sorted(glob.glob(os.path.join(pages, "*.xaml.cs"))):
        for n, line in _code_lines(_read(path)):
            if "RoutedEventArgs" in line:
                report(path, n, "R2", "page code-behind must not handle routed events; use a command")
            if DATACONTEXT_ASSIGN.search(line):
                report(path, n, "R2", "page code-behind must not assign DataContext")

    for path in sorted(glob.glob(os.path.join(root, "ViewModels", "*.cs"))):
        for n, line in _code_lines(_read(path)):
            m = VIEWMODEL_BANNED.search(line)
            if m:
                report(path, n, "R4", f"view models must not use {m.group(0)}; go through IShell")

    dialog_service = os.path.normpath(os.path.join(root, "Services", "DialogService.cs"))
    for path in sorted(glob.glob(os.path.join(root, "**", "*.cs"), recursive=True)):
        parts = os.path.normpath(os.path.relpath(path, root)).split(os.sep)
        if parts[0] in ("obj", "bin") or os.path.normpath(path) == dialog_service:
            continue
        for n, line in _code_lines(_read(path)):
            if re.search(r"\bMessageBox\.Show\b", line):
                report(path, n, "R5", "modal boxes go through Services/DialogService.cs")

    main_window = os.path.join(root, "Windows", "MainWindow.xaml")
    if os.path.exists(main_window):
        for n, line in enumerate(_read(main_window).splitlines(), 1):
            if re.search(r"<ScrollViewer\b", line):
                report(main_window, n, "R6", "MainWindow holds only chrome, navigation and page hosts; page content goes in Pages/")

    for rel in REQUIRED_FILES:
        path = os.path.join(root, *rel.split("/"))
        if not os.path.isfile(path):
            report(path, 0, "R7", "required file is missing")

    src = os.path.dirname(os.path.abspath(root))
    trees = [os.path.abspath(root)]
    core = os.path.join(src, "Envoy.Core")
    if os.path.isdir(core):
        trees.append(core)
    for tree in trees:
        for path in sorted(glob.glob(os.path.join(tree, "**", "*.cs"), recursive=True)):
            rel_src = os.path.relpath(path, src).replace(os.sep, "/")
            parts = rel_src.split("/")
            if "obj" in parts or "bin" in parts or rel_src == APPDATA_ALLOWED:
                continue
            for n, line in _code_lines(_read(path)):
                if APPDATA.search(line):
                    report(path, n, "R8", "only SettingsStore.DefaultPath names the application-data folder; use AppHost.DataFolder")

    for path in _app_sources(root):
        for n, line in _code_lines(_read(path)):
            if ASYNC_DISCARD.search(line):
                report(path, n, "R9", "fire-and-forget with `.Forget(log, what)`, not a `_ =` discard")

    compose = os.path.join(root, "Windows", "ComposeWindow.xaml.cs")
    if os.path.exists(compose):
        for n, line in _code_lines(_read(compose)):
            if "DialogService." in line:
                report(compose, n, "R10", "the reply box must not open a modal box; report a problem toast instead")
    app = os.path.join(root, "App.xaml.cs")
    if os.path.exists(app):
        warnings = [n for n, line in _code_lines(_read(app)) if "DialogService.ShowWarning" in line]
        for n in warnings[1:]:
            report(app, n, "R10", "DialogService.ShowWarning is only for the one crash-loop message")

    watcher = os.path.join(root, *WATCHER.split("/"))
    if os.path.exists(watcher):
        text = _read(watcher)
        lines = dict(_code_lines(text))
        for m in PROCESS_ASYNC.finditer(text):
            n = text.count("\n", 0, m.start()) + 1
            if n not in lines:
                continue  # a comment
            args = _call_args(text, m.end() - 1)
            parts = _split_args(args)
            if len(parts) < 2 or parts[-1].strip() != "CancellationToken.None":
                report(watcher, n, "R11", "the watcher must pass CancellationToken.None to ProcessAsync (contract §2.6)")
        for n, line in lines.items():
            if not line.lstrip().startswith("using ") and WATCHER_SETTINGS.search(line):
                report(watcher, n, "R11", "the watcher reads settings only through the CaptureOptions snapshot (contract §7)")

    _check_velopack(root, report)
    return problems


def _check_velopack(root, report):
    """UPD1 and UPD2 (spec 06): one place creates the update manager, one place runs Velopack, auto-apply stays off."""
    backend = os.path.normpath(os.path.join(root, *VELOPACK_BACKEND.split("/")))
    program = os.path.normpath(os.path.join(root, PROGRAM))
    sources = list(_app_sources(root))
    core = os.path.join(os.path.dirname(os.path.abspath(root)), "Envoy.Core")
    if os.path.isdir(core):
        sources += [p for p in sorted(glob.glob(os.path.join(core, "**", "*.cs"), recursive=True))
                    if not {"obj", "bin"} & set(os.path.relpath(p, core).split(os.sep))]
    for path in sources:
        here = os.path.normpath(path)
        for n, line in _code_lines(_read(path)):
            if NEW_UPDATE_MANAGER.search(line) and here != backend:
                report(path, n, "UPD1", "only Services/VelopackUpdateBackend.cs creates an UpdateManager")
            m = VELOPACK_BANNED.search(line)
            if m:
                report(path, n, "UPD1", f"{m.group(0)} is never used (no hosted update service; updates apply through the updater after exit)")
            if VELOPACK_BUILD.search(line) and here != program:
                report(path, n, "UPD2", "VelopackApp runs only in Program.Main, before any WPF object exists")
    if os.path.exists(program):
        text = _read(program)
        code = "\n".join(line for _, line in _code_lines(text))
        if VELOPACK_BUILD.search(code) and not AUTO_APPLY_OFF.search(code):
            report(program, 1, "UPD2", "Program.Main must call SetAutoApplyOnStartup(false): updates apply at start-up slot 5, after the mutex")


def _call_args(text, open_paren):
    """The text between the parenthesis at open_paren and its match."""
    depth = 0
    for i in range(open_paren, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return text[open_paren + 1:i]
    return text[open_paren + 1:]


def _split_args(args):
    """Top-level comma-separated arguments."""
    parts, depth, start = [], 0, 0
    for i, c in enumerate(args):
        if c in "([{<":
            depth += 1
        elif c in ")]}>":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(args[start:i])
            start = i + 1
    parts.append(args[start:])
    return [p for p in parts if p.strip()]


def _app_sources(root):
    for path in sorted(glob.glob(os.path.join(root, "**", "*.cs"), recursive=True)):
        parts = os.path.normpath(os.path.relpath(path, root)).split(os.sep)
        if parts[0] not in ("obj", "bin"):
            yield path


def main(argv):
    if len(argv) != 2:
        print("usage: check_ui_rules.py <src/Envoy.App dir>", file=sys.stderr)
        return 2
    problems = check(argv[1])
    if problems:
        print("\n".join(problems))
        return 1
    print("ui rules ok")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
