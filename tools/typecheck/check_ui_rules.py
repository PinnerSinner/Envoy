"""Mechanical checks for the UI-structure rules of spec 01 (stdlib only).

Usage: check_ui_rules.py <src/Envoy.App dir>. Prints `path:line: UIF-Rn message` per violation and exits 1 if any.

R1  Pages/*.xaml has no event-handler attribute (the gen_stubs matcher)               REQ-UIF-7
R2  Pages/*.xaml.cs contains no RoutedEventArgs and no `DataContext =`                REQ-UIF-7, REQ-UIF-2
R3  Pages/*.xaml sets no DataContext (attribute or property element)                  REQ-UIF-2
R4  ViewModels/*.cs contain no App.Current, Process.Start, ProcessStartInfo, MessageBox  REQ-UIF-9
R5  MessageBox.Show appears only in Services/DialogService.cs                         REQ-UIF-9
R6  Windows/MainWindow.xaml contains no ScrollViewer                                  REQ-UIF-1
R7  The page, view-model and service files of spec 01 exist                           REQ-UIF-1, REQ-UIF-21
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
        problems.append(f"{_rel(root, path)}:{line}: UIF-{rule} {message}")

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

    return problems


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
