# Generates stand-ins for the code the WPF markup compiler would emit (InitializeComponent + x:Name fields)
# so the app's C# can be type-checked on Linux, where the WindowsDesktop SDK is not available.
import re, sys, os, glob
import xml.etree.ElementTree as ET
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
WPF = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
X = "http://schemas.microsoft.com/winfx/2006/xaml"
for path in glob.glob(f"{src}/**/*.xaml", recursive=True):
    tree = ET.parse(path); root = tree.getroot()
    cls = root.get(f"{{{X}}}Class")
    if not cls: continue
    ns, name = cls.rsplit(".",1)
    nsmap = {}
    for _, (prefix, uri) in ET.iterparse(path, events=["start-ns"]):
        nsmap[uri] = prefix
    fields = []
    def walk(el, in_template):
        tag = el.tag
        uri, local = tag[1:].split("}") if tag.startswith("{") else ("", tag)
        if local in ("ControlTemplate","DataTemplate","ItemsPanelTemplate"): in_template = True
        n = el.get(f"{{{X}}}Name")
        if n and not in_template and "." not in local:
            if uri == WPF: t = local
            elif uri.startswith("clr-namespace:"): t = uri.split(":")[1].split(";")[0] + "." + local
            else: t = local
            fields.append((t, n))
        for c in el: walk(c, in_template)
    walk(root, False)
    base = root.tag.split("}")[1]
    lines = [f"namespace {ns};", "#pragma warning disable", "using System.Windows; using System.Windows.Controls; using System.Windows.Controls.Primitives; using System.Windows.Media; using System.Windows.Documents; using System.Windows.Shapes;",
             f"public partial class {name} {{"]
    for t, n in fields:
        lines.append(f"  internal {t} {n} = null!;")
    lines.append("  public void InitializeComponent() {}")
    lines.append("}")
    open(os.path.join(out, name + ".g.cs"), "w").write("\n".join(lines))
    print(name, len(fields))
