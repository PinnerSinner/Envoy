"""Writes every src/Envoy.App/Themes/Palette.*.xaml (spec 09a), so the seven palettes keep the same keys.

Usage: python3 tools/themes/make_palettes.py [themes dir]   (default: src/Envoy.App/Themes)

The herald tokens come from docs/design/herald/Tokens.dc.html. A livery changes only its primary and secondary
tinctures, its darker shade and its wax; high contrast maps every key to a SystemColors member. After a change run the
Core tests (PaletteContrastTests, ThemeManagerTests) and tools/typecheck/typecheck.sh.
"""
import os
import sys

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "src", "Envoy.App", "Themes")

BASE = {
    # existing keys, herald values
    "Bg.Window": "#E2CDA2", "Bg.Panel": "#EAD8B4", "Bg.PanelAlt": "#F4E8CC", "Bg.Input": "#F1E3C2",
    "Text.Primary": "#2A1D12", "Text.Secondary": "#3F2D1A", "Text.Muted": "#5C4529", "Text.OnAccent": "#F8E3C9",
    "Border.Subtle": "#B89A63", "Danger": "#8A1C12", "Success": "#3F6B3A",
    "Overlay.Text": "#2A1D12", "Overlay.Original": "#5C4529", "Overlay.Bg": "#EAD8B4",
    "Focus.Ring": "#7A5410",
    # herald tokens (Tokens.dc.html)
    "Gules": "#8E2418", "Gules.Dark": "#6E1A10", "Or": "#D8B04A", "Or.Light": "#E9CC7A", "Or.Dark": "#8C6516",
    "Vellum": "#EAD8B4", "Vellum.Light": "#F6EBD2", "Vellum.Dark": "#D3B987",
    "Oak": "#3A2414", "Oak.Deep": "#2B1A0E", "Oak.Tab": "#4A3020", "Ink": "#2A1D12", "Ink.Soft": "#5C4529",
    "Leather.Text": "#D9C298", "Leather.Muted": "#B89A63", "Focus.Ring.Oak": "#E9CC7A",
    "Ornament.Gold": "#D8B04A", "Ornament.Pip": "#F3D98C", "Ornament.Edge": "#B98B2E", "Ornament.Filigree": "#8C6516",
    "Ornament.Rule": "#7A5A2E", "Wax.Text": "#F8E3C9",
    "Wax.Gules.Light": "#D45542", "Wax.Gules.Dark": "#8E1F14",
    "Wax.Azure.Light": "#4B6FB4", "Wax.Azure.Dark": "#1E3460",
    "Wax.Vert.Light": "#5E8C49", "Wax.Vert.Dark": "#2B4A22",
    "Wax.Or.Light": "#B58A2E", "Wax.Or.Dark": "#6B4E12",
    "Wax.Purpure.Light": "#8A58A6", "Wax.Purpure.Dark": "#3F1D52",
    "Wax.Sable.Light": "#5A5650", "Wax.Sable.Dark": "#1C1A18",
    "Wax.Plain.Light": "#CDBFA2", "Wax.Plain.Dark": "#9C8D70",
}

LIVERIES = {
    # id: primary, primary.dark (band, hover), secondary, wax, title
    "herald": ("#8E2418", "#6E1A10", "#D8B04A", "Gules", "the herald's own arms, gules and or"),
    "imperial": ("#8E2418", "#6E1A10", "#D8B04A", "Gules", "Age of Empires IV, gules and or"),
    "parchment": ("#2B4C8C", "#1E3460", "#D8B04A", "Azure", "Age of Empires II, azure and or"),
    "frontier": ("#2F5E35", "#21432A", "#E9E4D6", "Vert", "Age of Empires III, vert and argent"),
    "olympian": ("#5B2D73", "#3F1D52", "#D8B04A", "Purpure", "Age of Mythology, purpure and or"),
    "classic": ("#1C1A18", "#0F0E0D", "#D8B04A", "Sable", "Age of Empires, sable and or"),
}

HC = {  # high contrast: SystemColors members
    "Bg.Window": "WindowColor", "Bg.Panel": "WindowColor", "Bg.PanelAlt": "WindowColor", "Bg.Input": "WindowColor",
    "Accent": "HighlightColor", "Accent.Bright": "HighlightColor", "Accent.Dim": "WindowTextColor",
    "Text.Primary": "WindowTextColor", "Text.Secondary": "WindowTextColor", "Text.Muted": "GrayTextColor",
    "Text.OnAccent": "HighlightTextColor", "Border.Subtle": "WindowTextColor", "Danger": "WindowTextColor",
    "Success": "WindowTextColor", "Overlay.Text": "WindowTextColor", "Overlay.Sender": "WindowTextColor",
    "Overlay.Original": "GrayTextColor", "Overlay.Bg": "WindowColor", "Focus.Ring": "HighlightColor",
}

ORDER = ["Bg.Window", "Bg.Panel", "Bg.PanelAlt", "Bg.Input", "Accent", "Accent.Bright", "Accent.Dim", "Text.Primary",
         "Text.Secondary", "Text.Muted", "Text.OnAccent", "Border.Subtle", "Danger", "Success", "Overlay.Text",
         "Overlay.Sender", "Overlay.Original", "Overlay.Bg", "Focus.Ring"]
NEW = ["Gules", "Gules.Dark", "Or", "Or.Light", "Or.Dark", "Vellum", "Vellum.Light", "Vellum.Dark", "Oak", "Oak.Deep",
       "Oak.Tab", "Ink", "Ink.Soft", "Leather.Text", "Leather.Muted", "Focus.Ring.Oak", "Livery.Primary",
       "Livery.Secondary", "Livery.Dark", "Ornament.Gold", "Ornament.Pip", "Ornament.Edge", "Ornament.Filigree",
       "Ornament.Rule", "Wax.Text"]
WAXES = ["Gules", "Azure", "Vert", "Or", "Purpure", "Sable", "Plain", "Livery"]


def hc_member(key):
    if key in HC:
        return HC[key]
    if key.startswith(("Oak", "Livery.Dark")):
        return "ControlColor"
    if key in ("Leather.Text", "Leather.Muted"):
        return "ControlTextColor"
    if key in ("Focus.Ring.Oak",):
        return "HighlightColor"
    if key == "Wax.Text":
        return "HighlightTextColor"
    if key.startswith("Wax."):
        return "HighlightColor"
    if key.startswith("Vellum"):
        return "WindowColor"
    if key in ("Ink", "Ink.Soft", "Ornament.Filigree", "Ornament.Rule", "Livery.Primary", "Gules", "Gules.Dark",
               "Or.Dark"):
        return "WindowTextColor"
    return "HighlightColor"


def colours(pid):
    if pid == "highcontrast":
        return None
    p, pd, s, wax, _ = LIVERIES[pid]
    c = dict(BASE)
    c.update({"Accent": p, "Accent.Bright": pd, "Accent.Dim": "#8C6516", "Overlay.Sender": p,
              "Livery.Primary": p, "Livery.Secondary": s, "Livery.Dark": pd,
              "Wax.Livery.Light": BASE[f"Wax.{wax}.Light"], "Wax.Livery.Dark": BASE[f"Wax.{wax}.Dark"]})
    return c


def colour_keys():
    keys = ORDER + NEW
    for w in WAXES:
        keys += [f"Wax.{w}.Light", f"Wax.{w}.Dark"]
    return keys


def gen(pid):
    hc = pid == "highcontrast"
    c = colours(pid)
    L = []
    w = L.append
    w('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
    w('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
    w('                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"')
    w('                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"')
    w('                    mc:Ignorable="po">')
    if hc:
        w('  <!--')
        w('    High-contrast palette (spec 07b, REQ-OUXB-32): every colour is a Windows system colour, read when this dictionary')
        w('    loads (ThemeManager reloads it on a contrast-theme change). Chosen by ThemeResolver, never listed in the theme')
        w('    picker. The only literals are the two functional alphas, the same in every palette. Every key here must exist in')
        w('    every Palette.*.xaml. The herald ornaments (spec 09a) become flat system colours: no gradients, no knotwork.')
        w('  -->')
    else:
        title = LIVERIES[pid][4]
        w(f'  <!-- {pid} livery of the Herald\'s Writ look (spec 09a): {title}. Generated by tools/themes/make_palettes.py, so every')
        w('       Palette.*.xaml has the same keys; oak, vellum and ink are shared, the livery colours and wax differ. -->')
    for k in colour_keys():
        if hc:
            w(f'  <x:Static x:Key="{k}.Color" Member="SystemColors.{hc_member(k)}" />')
        else:
            w(f'  <Color x:Key="{k}.Color">{c[k]}</Color>')
    w('  <!-- Functional alphas (not decorative): the region selector\'s hit-test surface and dim. Same in every palette. -->')
    w('  <Color x:Key="Overlay.HitTest.Color">#01000000</Color>')
    w('  <Color x:Key="Overlay.Dim.Color">#99000000</Color>')
    solid = [k for k in colour_keys() if not k.startswith("Wax.") or k == "Wax.Text"] + ["Overlay.HitTest", "Overlay.Dim"]
    for k in solid:
        w(f'  <SolidColorBrush x:Key="{k}" Color="{{StaticResource {k}.Color}}" po:Freeze="True" />')
    S = lambda k: f'{{StaticResource {k}.Color}}'
    if hc:
        for wx in WAXES:
            w(f'  <SolidColorBrush x:Key="Wax.{wx}" Color="{S(f"Wax.{wx}.Dark")}" po:Freeze="True" />')
        flat = {"Header.Gradient": "Bg.Window", "Accent.Gradient": "Accent", "Rule.Gradient": "Text.Muted",
                "Vellum.Gradient": "Bg.Panel", "Oak.Gradient": "Oak", "Gold.Gradient": "Accent",
                "DropCap.Gradient": "Accent", "Boss.Gradient": "Accent", "Rod.Gradient": "Text.Primary",
                "Plaque.Gradient": "Bg.Input", "Knot.Horizontal": "Text.Primary", "Knot.Vertical": "Text.Primary",
                "Damask.Oak": "Oak", "Crest.Field": "Accent", "Candle.Flame": "Accent", "Candle.Wax": "Bg.Panel",
                "Row.Rule": "Overlay.HitTest"}
        for k, v in flat.items():
            w(f'  <SolidColorBrush x:Key="{k}" Color="{S(v)}" po:Freeze="True" />')
        w('</ResourceDictionary>')
        return "\n".join(L) + "\n"
    for wx in WAXES:
        w(f'  <RadialGradientBrush x:Key="Wax.{wx}" GradientOrigin="0.35,0.3" Center="0.5,0.5" RadiusX="0.7" RadiusY="0.7" po:Freeze="True">'
          f'<GradientStop Offset="0" Color="{S(f"Wax.{wx}.Light")}" /><GradientStop Offset="1" Color="{S(f"Wax.{wx}.Dark")}" /></RadialGradientBrush>')
    p, pd, s, wax, _ = LIVERIES[pid]
    def lg(key, start, end, stops):
        st = "".join(f'<GradientStop Offset="{o}" Color="{col}" />' for o, col in stops)
        w(f'  <LinearGradientBrush x:Key="{key}" StartPoint="{start}" EndPoint="{end}" po:Freeze="True">{st}</LinearGradientBrush>')
    lg("Header.Gradient", "0,0", "0,1", [(0, "#3A2414"), (1, "#2B1A0E")])
    lg("Accent.Gradient", "0,0", "1,0", [(0, pd), (1, p)])
    lg("Rule.Gradient", "0,0", "1,0", [(0, "#00B98B2E"), (0.5, "#B98B2E"), (1, "#00B98B2E")])
    w('  <RadialGradientBrush x:Key="Vellum.Gradient" GradientOrigin="0.25,0.15" Center="0.25,0.15" RadiusX="1.1" RadiusY="1.1" po:Freeze="True">'
      '<GradientStop Offset="0" Color="#F6EBD2" /><GradientStop Offset="0.5" Color="#EAD8B4" /><GradientStop Offset="1" Color="#D7BE91" /></RadialGradientBrush>')
    lg("Oak.Gradient", "0,0", "1,0", [(0, "#592B1A0E"), (1, "#0D2B1A0E")])
    lg("Gold.Gradient", "0,0", "1,0.3", [(0, "#9A7428"), (0.3, "#E9CC7A"), (0.42, "#FFF4C8"), (0.54, "#E9CC7A"), (1, "#9A7428")])
    lg("DropCap.Gradient", "0,0", "1,1", [(0, "#F3D98C"), (0.6, "#B98B2E"), (1, "#8C6516")])
    w('  <RadialGradientBrush x:Key="Boss.Gradient" GradientOrigin="0.35,0.3" Center="0.5,0.5" RadiusX="0.7" RadiusY="0.7" po:Freeze="True">'
      '<GradientStop Offset="0" Color="#F3D98C" /><GradientStop Offset="1" Color="#9A7428" /></RadialGradientBrush>')
    lg("Rod.Gradient", "0,0", "0,1", [(0, "#7E5D30"), (0.5, "#E3CDA0"), (1, "#6E5028")])
    lg("Plaque.Gradient", "0,0", "0,1", [(0, "#F7EDD5"), (1, "#DCC596")])
    # the crest: the livery's two tinctures split on the diagonal
    lg("Crest.Field", "0,0", "1,1", [(0, p), (0.5, p), (0.5, s), (1, s)])
    w('  <RadialGradientBrush x:Key="Candle.Flame" GradientOrigin="0.5,0.7" Center="0.5,0.7" RadiusX="0.6" RadiusY="0.6" po:Freeze="True">'
      '<GradientStop Offset="0" Color="#FFF3C4" /><GradientStop Offset="0.45" Color="#FFB54D" /><GradientStop Offset="1" Color="#00FF7828" /></RadialGradientBrush>')
    # a settings row's top rule: 1 px of ruling, then nothing (absolute mapping, so every row height gets the same line)
    w('  <LinearGradientBrush x:Key="Row.Rule" MappingMode="Absolute" StartPoint="0,0" EndPoint="0,2" po:Freeze="True">'
      '<GradientStop Offset="0" Color="#80B89A63" /><GradientStop Offset="0.5" Color="#80B89A63" /><GradientStop Offset="0.5" Color="#00B89A63" /></LinearGradientBrush>')
    lg("Candle.Wax", "0,0", "1,0", [(0, "#E2D4B4"), (0.5, "#FBF4E2"), (1, "#D3C29A")])
    # knotwork band tiles (WarRoom.dc.html band(): 28 x 18, two crossing curves and gold pips)
    for key, vp, rect, curves, pips in [
        ("Knot.Horizontal", "0,0,28,18", "M0,0 H28 V18 H0 Z", "M0,9 C7,-1 21,-1 28,9 M0,9 C7,19 21,19 28,9", [(14, 9, 2), (0, 9, 1.4), (28, 9, 1.4)]),
        ("Knot.Vertical", "0,0,18,28", "M0,0 H18 V28 H0 Z", "M9,0 C-1,7 -1,21 9,28 M9,0 C19,7 19,21 9,28", [(9, 14, 2), (9, 0, 1.4), (9, 28, 1.4)])]:
        el = "".join(f'<EllipseGeometry Center="{x},{y}" RadiusX="{r}" RadiusY="{r}" />' for x, y, r in pips)
        w(f'  <DrawingBrush x:Key="{key}" TileMode="Tile" Viewport="{vp}" ViewportUnits="Absolute" Viewbox="{vp}" ViewboxUnits="Absolute" Stretch="None" po:Freeze="True">')
        w('    <DrawingBrush.Drawing><DrawingGroup>')
        w(f'      <GeometryDrawing Brush="{{StaticResource Livery.Dark}}" Geometry="{rect}" />')
        w(f'      <GeometryDrawing Geometry="{curves}"><GeometryDrawing.Pen><Pen Brush="{{StaticResource Ornament.Gold}}" Thickness="1.6" /></GeometryDrawing.Pen></GeometryDrawing>')
        w(f'      <GeometryDrawing Brush="{{StaticResource Ornament.Pip}}"><GeometryDrawing.Geometry><GeometryGroup>{el}</GeometryGroup></GeometryDrawing.Geometry></GeometryDrawing>')
        w('    </DrawingGroup></DrawingBrush.Drawing>')
        w('  </DrawingBrush>')
    # damask: the oak wall's lozenge lattice with quatrefoils (WarRoom.dc.html mountWall lattice), flat
    w('  <DrawingBrush x:Key="Damask.Oak" TileMode="Tile" Viewport="0,0,128,128" ViewportUnits="Absolute" Viewbox="0,0,128,128" ViewboxUnits="Absolute" Stretch="None" po:Freeze="True">')
    w('    <DrawingBrush.Drawing><DrawingGroup>')
    w('      <GeometryDrawing Brush="{StaticResource Oak}" Geometry="M0,0 H128 V128 H0 Z" />')
    w('      <GeometryDrawing Geometry="M64,0 L128,64 L64,128 L0,64 Z"><GeometryDrawing.Pen><Pen Brush="#559A7838" Thickness="1.5" /></GeometryDrawing.Pen></GeometryDrawing>')
    w('      <GeometryDrawing Geometry="M64,11 L117,64 L64,117 L11,64 Z"><GeometryDrawing.Pen><Pen Brush="#339A7838" Thickness="1" DashStyle="{x:Static DashStyles.Dash}" /></GeometryDrawing.Pen></GeometryDrawing>')
    foils = []
    for cx, cy, r in [(64, 64, 6.5), (0, 0, 4.5), (128, 0, 4.5), (0, 128, 4.5), (128, 128, 4.5)]:
        for dx, dy in [(r, 0), (-r, 0), (0, r), (0, -r)]:
            foils.append(f'<EllipseGeometry Center="{cx + dx},{cy + dy}" RadiusX="{r}" RadiusY="{r}" />')
    w(f'      <GeometryDrawing Brush="#447A5428"><GeometryDrawing.Geometry><GeometryGroup>{"".join(foils)}</GeometryGroup></GeometryDrawing.Geometry></GeometryDrawing>')
    w('    </DrawingGroup></DrawingBrush.Drawing>')
    w('  </DrawingBrush>')
    w('</ResourceDictionary>')
    return "\n".join(L) + "\n"


for pid in list(LIVERIES) + ["highcontrast"]:
    with open(f"{OUT}/Palette.{pid}.xaml", "w", encoding="utf-8", newline="\n") as f:
        f.write(gen(pid))
print("ok")
