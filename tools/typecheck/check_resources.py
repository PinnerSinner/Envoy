"""Fails if any XAML uses a StaticResource/DynamicResource key that no theme dictionary defines,
or if the palette files don't all define the same keys (a theme switch would leave holes)."""
import glob, os, re, sys

root = sys.argv[1]
themes = os.path.join(root, "Themes")
defined = set()
for f in glob.glob(os.path.join(themes, "*.xaml")):
    text = open(f, encoding="utf-8").read()
    defined |= set(re.findall(r'x:Key="([^"]+)"', text))
    # An implicit style (no x:Key) is keyed by its target type, so BasedOn="{StaticResource {x:Type T}}" can reach it.
    for attrs in re.findall(r'<Style\b([^>]*)>', text):
        target = re.search(r'TargetType="([^"{]+)"', attrs)
        if target and "x:Key=" not in attrs:
            defined.add("{x:Type %s}" % target.group(1))

palettes = {os.path.basename(f): set(re.findall(r'x:Key="([^"]+)"', open(f, encoding="utf-8").read()))
            for f in glob.glob(os.path.join(themes, "Palette.*.xaml"))}
problems = []
reference = next(iter(palettes.values()))
for name, keys in palettes.items():
    if keys != reference:
        problems.append(f"{name}: palette keys differ ({sorted(keys ^ reference)})")

def local_keys(xaml):
    """Keys a window or control defines in its own resources (for example OverlayWindow's item templates)."""
    if not os.path.exists(xaml):
        return set()
    return set(re.findall(r'x:Key="([^"]+)"', open(xaml, encoding="utf-8").read()))

for f in glob.glob(os.path.join(root, "**", "*.xaml"), recursive=True):
    text = open(f, encoding="utf-8").read()
    local = local_keys(f)
    for kind, key in re.findall(r'\{(StaticResource|DynamicResource) (\{x:Type [^}]+\}|[^}]+)\}', text):
        # A palette key must come from the themes; a window's own keys only serve StaticResource lookups.
        if key not in defined and not (kind == "StaticResource" and key in local):
            problems.append(f"{os.path.relpath(f, root)}: {kind} '{key}' is not defined")
for f in glob.glob(os.path.join(root, "**", "*.cs"), recursive=True):
    local = local_keys(f[:-3]) if f.endswith(".xaml.cs") else set()
    for key in re.findall(r'(?:FindResource|TryFindResource)\("([^"]+)"\)', open(f, encoding="utf-8").read()):
        if key not in defined and key not in local:
            problems.append(f"{os.path.relpath(f, root)}: resource '{key}' is not defined")

if problems:
    print("\n".join(problems))
    sys.exit(1)
print(f"resources ok ({len(defined)} keys, {len(palettes)} palettes)")
