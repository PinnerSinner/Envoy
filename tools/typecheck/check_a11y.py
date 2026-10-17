"""Accessibility lint for the WPF XAML (spec 07b, stdlib only).

Usage: check_a11y.py <src/Envoy.App dir>. Prints `path:line: A11Yn message` per violation and exits 1 if any.

A11Y1 FocusVisualStyle is never {x:Null} (a Style setter or an attribute) unless the same Style or element also sets
      Focusable to False                                                                                REQ-OUXB-24
A11Y2 Button, ToggleButton, TextBox, PasswordBox, ComboBox, Slider and HotkeyBox outside a ControlTemplate have
      AutomationProperties.Name or AutomationProperties.LabeledBy. A Button is exempt when its literal Content
      contains a letter outside the private-use glyph range U+E000-U+F8FF and its Style is not Button.Chrome. A
      SecretBox is a PasswordBox with the attached SecretBox.Value, so A11Y2 covers it.                REQ-OUXB-26
A11Y3 A CheckBox or RadioButton without a Content attribute has a Name or LabeledBy                     REQ-OUXB-26
A11Y4 No Color, Background, Foreground, BorderBrush, Fill or Stroke attribute starts with '#' in Windows/, Pages/
      or Controls/ (palette keys only; DropShadowEffect Color="Black" is allowed)                       REQ-OUXB-35
A11Y5 The root Window of SetupWizardWindow.xaml and ComposeWindow.xaml sets
      KeyboardNavigation.TabNavigation="Cycle"                                                          REQ-OUXB-29
"""
import glob
import os
import sys
import xml.parsers.expat

NAMED = {"Button", "ToggleButton", "TextBox", "PasswordBox", "ComboBox", "Slider", "HotkeyBox"}
CHOICE = {"CheckBox", "RadioButton"}
COLOUR_ATTRS = {"Color", "Background", "Foreground", "BorderBrush", "Fill", "Stroke"}
COLOUR_DIRS = ("Windows/", "Pages/", "Controls/")
CYCLE_WINDOWS = {"Windows/SetupWizardWindow.xaml", "Windows/ComposeWindow.xaml"}
NAME_ATTRS = ("AutomationProperties.Name", "AutomationProperties.LabeledBy")
NULL = "{x:Null}"


class Node:
    def __init__(self, tag, attrs, line, parent):
        self.tag = tag.split(":")[-1]
        self.attrs = attrs
        self.line = line
        self.parent = parent
        self.children = []

    def ancestors(self):
        node = self.parent
        while node is not None:
            yield node
            node = node.parent


def parse(path):
    """A light tree with line numbers (expat reports them; ElementTree does not)."""
    root = None
    stack = []
    parser = xml.parsers.expat.ParserCreate()

    def start(tag, attrs):
        nonlocal root
        parent = stack[-1] if stack else None
        node = Node(tag, attrs, parser.CurrentLineNumber, parent)
        if parent is not None:
            parent.children.append(node)
        else:
            root = node
        stack.append(node)

    def end(_tag):
        stack.pop()

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    with open(path, "rb") as f:
        parser.ParseFile(f)
    return root


def walk(node):
    yield node
    for child in node.children:
        yield from walk(child)


def _has_name(node):
    if any(node.attrs.get(a) for a in NAME_ATTRS):
        return True
    # Property-element form: <AutomationProperties.Name>…</AutomationProperties.Name>
    return any(c.tag in NAME_ATTRS for c in node.children)


def _letter_content(node):
    content = node.attrs.get("Content")
    if not content or content.startswith("{"):
        return False
    return any(ch.isalpha() and not 0xE000 <= ord(ch) <= 0xF8FF for ch in content)


def _setter(style, prop):
    for child in style.children:
        if child.tag == "Setter" and child.attrs.get("Property") == prop:
            return child.attrs.get("Value")
    return None


def check_file(path, rel):
    problems = []
    root = parse(path)

    def report(node, rule, message):
        problems.append(f"{rel}:{node.line}: {rule} {message}")

    for node in walk(root):
        # A11Y1
        if node.tag == "Setter" and node.attrs.get("Property") == "FocusVisualStyle" and node.attrs.get("Value") == NULL:
            style = node.parent
            if style is None or style.tag != "Style" or _setter(style, "Focusable") != "False":
                report(node, "A11Y1", "FocusVisualStyle is {x:Null} on a focusable style; use FocusVisual.Ring")
        if node.attrs.get("FocusVisualStyle") == NULL and node.attrs.get("Focusable") != "False":
            report(node, "A11Y1", f"{node.tag} sets FocusVisualStyle to {{x:Null}} but is focusable; use FocusVisual.Ring")

        in_template = any(a.tag == "ControlTemplate" for a in node.ancestors())
        # A11Y2
        if node.tag in NAMED and not in_template and not _has_name(node):
            chrome = node.attrs.get("Style", "").replace(" ", "") == "{StaticResourceButton.Chrome}"
            exempt = node.tag == "Button" and _letter_content(node) and not chrome
            if not exempt:
                label = node.attrs.get("x:Name") or node.attrs.get("Content") or ""
                report(node, "A11Y2", f"{node.tag} {label!r} has no AutomationProperties.Name or LabeledBy".replace(" ''", ""))
        # A11Y3
        if node.tag in CHOICE and not in_template and "Content" not in node.attrs and not _has_name(node):
            report(node, "A11Y3", f"{node.tag} without Content has no AutomationProperties.Name or LabeledBy")
        # A11Y4
        if rel.startswith(COLOUR_DIRS):
            for attr in COLOUR_ATTRS:
                value = node.attrs.get(attr, "")
                if value.strip().startswith("#"):
                    report(node, "A11Y4", f"literal colour {attr}=\"{value}\"; use a palette key")

    # A11Y5
    if rel in CYCLE_WINDOWS and root.attrs.get("KeyboardNavigation.TabNavigation") != "Cycle":
        report(root, "A11Y5", 'the root Window needs KeyboardNavigation.TabNavigation="Cycle"')
    return problems


def check(app_dir):
    problems = []
    for path in sorted(glob.glob(os.path.join(app_dir, "**", "*.xaml"), recursive=True)):
        rel = os.path.relpath(path, app_dir).replace(os.sep, "/")
        if rel.startswith(("obj/", "bin/")):
            continue
        problems.extend(check_file(path, rel))
    return problems


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    problems = check(argv[1])
    if problems:
        print("\n".join(problems))
        return 1
    print("a11y ok")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
