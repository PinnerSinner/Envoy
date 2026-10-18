"""Builds the animated README art for Envoy in the Herald's Writ style.

Every SVG embeds subset fonts (OFL) as data URIs so GitHub renders it the same for everyone.
Usage: python3 make_svgs.py <fonts dir> <out dir>
"""
import base64, io, os, sys
from fontTools import subset
from fontTools.ttLib import TTFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flame import flame, flame_defs

FONTS, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)

# ---------- palette (docs/design/herald/Tokens.dc.html) ----------
GULES, GULES_D, OR, OR_L, OR_D, OR_DD = '#8E2418', '#6E1A10', '#D8B04A', '#E9CC7A', '#B98B2E', '#8C6516'
VELLUM, INK, INK_S, OAK, OAK_D, LEATHER = '#EAD8B4', '#2A1D12', '#5C4529', '#3A2414', '#2B1A0E', '#D9C298'

def font_face(family, file, text, style='normal', weight='400'):
    f = TTFont(os.path.join(FONTS, file))
    opts = subset.Options(); opts.flavor = 'woff2'; opts.layout_features = ['*']
    s = subset.Subsetter(opts); s.populate(text=text + ' .,:;!?()&0123456789'); s.subset(f)
    buf = io.BytesIO(); f.flavor = 'woff2'; f.save(buf)
    b64 = base64.b64encode(buf.getvalue()).decode()
    return f"@font-face{{font-family:'{family}';font-style:{style};font-weight:{weight};src:url(data:font/woff2;base64,{b64}) format('woff2')}}"

def fonts(texts):
    t = ''.join(texts)
    return ''.join([
        font_face('Frak', 'unifrakturmaguntia-latin-400-normal.woff2', t),
        font_face('FellSC', 'im-fell-english-sc-latin-400-normal.woff2', t),
        font_face('Fell', 'im-fell-english-latin-400-italic.woff2', t, 'italic'),
        font_face('Gara', 'eb-garamond-latin-400-normal.woff2', t),
        font_face('Gara', 'eb-garamond-latin-600-normal.woff2', t, 'normal', '600'),
    ])

CJK = "'Microsoft YaHei','Malgun Gothic','Noto Sans CJK SC','Noto Sans CJK KR','Apple SD Gothic Neo','PingFang SC',sans-serif"

def svg(w, h, body, texts, title, extra_css=''):
    css = fonts(texts) + """
.frak{font-family:'Frak',serif}.sc{font-family:'FellSC',serif}.it{font-family:'Fell',serif;font-style:italic}
.ga{font-family:'Gara',%s}.cjk{font-family:'Gara',%s}
@media (prefers-reduced-motion: reduce){*{animation:none!important}}
""" % (CJK, CJK) + extra_css
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-label="{title}">
<title>{title}</title>
<style>{css}</style>
{body}
</svg>
'''

# ---------- shared defs ----------
def defs(uid):
    return f'''<defs>
  <linearGradient id="oak{uid}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2f1d10"/><stop offset="1" stop-color="#1a0f08"/></linearGradient>
  <radialGradient id="vig{uid}" cx="50%" cy="50%" r="70%"><stop offset=".45" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".7"/></radialGradient>
  <radialGradient id="vel{uid}" cx="28%" cy="22%" r="85%"><stop offset="0" stop-color="#F6EBD2"/><stop offset=".55" stop-color="#E9D6B0"/><stop offset="1" stop-color="#D3B987"/></radialGradient>
  <pattern id="dam{uid}" width="64" height="64" patternUnits="userSpaceOnUse">
    <path d="M32 0L64 32L32 64L0 32Z" fill="none" stroke="{OR_D}" stroke-opacity=".32" stroke-width="1.4"/>
    <path d="M32 9L55 32L32 55L9 32Z" fill="none" stroke="{OR}" stroke-opacity=".16" stroke-dasharray="3 3"/>
    <g fill="{OR_D}" fill-opacity=".35"><circle cx="28" cy="32" r="4"/><circle cx="36" cy="32" r="4"/><circle cx="32" cy="28" r="4"/><circle cx="32" cy="36" r="4"/></g>
  </pattern>
  <pattern id="knotH{uid}" width="28" height="18" patternUnits="userSpaceOnUse"><rect width="28" height="18" fill="{GULES_D}"/><path d="M0 9C7 -1 21 -1 28 9M0 9C7 19 21 19 28 9" fill="none" stroke="{OR}" stroke-width="1.6"/><circle cx="14" cy="9" r="2" fill="#F3D98C"/><circle cx="0" cy="9" r="1.4" fill="#F3D98C"/><circle cx="28" cy="9" r="1.4" fill="#F3D98C"/></pattern>
  <radialGradient id="waxR{uid}" cx="36%" cy="30%" r="70%"><stop offset="0" stop-color="#D45542"/><stop offset=".55" stop-color="#9B2C1F"/><stop offset="1" stop-color="#6E1A10"/></radialGradient>
  <radialGradient id="flame{uid}" cx="50%" cy="80%" r="70%"><stop offset="0" stop-color="#FFF6D0"/><stop offset=".3" stop-color="#FFC45A"/><stop offset=".65" stop-color="#F07A22" stop-opacity=".75"/><stop offset="1" stop-color="#C83C0A" stop-opacity="0"/></radialGradient>
  {flame_defs(uid)}
  <radialGradient id="glowold{uid}"><stop offset="0" stop-color="#FFA646" stop-opacity=".55"/><stop offset=".45" stop-color="#B4501E" stop-opacity=".18"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>
</defs>'''

def torch(x, y, uid, delay=0.0, s=1.0, seed=1):
    return f'''<g transform="translate({x} {y}) scale({s})">
  <rect x="-13" y="80" width="26" height="40" rx="3" fill="#1b1714"/><rect x="-13" y="80" width="26" height="4" fill="#3a332c"/>
  <path d="M-6 4L-4 96H4L6 4Z" fill="#3e2a17"/><path d="M-2 6L-1 94" stroke="#6b4a2a" stroke-width="1.2"/>
  <path d="M-19 -2C-19 10 -12 16 0 16C12 16 19 10 19 -2Z" fill="#26201b"/><path d="M-19 -2H19" stroke="#4a4038" stroke-width="2.5"/>
  <path d="M-15 -4C-13 -10 13 -10 15 -4" fill="#2d1a0c"/>
  <g transform="translate(0 -4)">{flame(uid, seed)}</g>
</g>'''

def embers(n, xs, y0, rise, uid, seed=1):
    out = []
    import random
    r = random.Random(seed)
    for i in range(n):
        x = xs[i % len(xs)] + r.uniform(-24, 24)
        dx = r.uniform(-60, 60)
        dur = r.uniform(2.4, 4.4)
        beg = r.uniform(0, 4)
        rad = r.uniform(1.2, 2.6)
        out.append(f'<circle cx="{x:.0f}" cy="{y0}" r="{rad:.1f}" fill="#FFC870"><animate attributeName="cy" from="{y0}" to="{y0 - rise}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/><animate attributeName="cx" from="{x:.0f}" to="{x + dx:.0f}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/><animate attributeName="opacity" values="0;1;.8;0" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/></circle>')
    return '\n'.join(out)

def fleuron(cx, cy, w, color=OR_L, accent='#B4382A', draw=True, uid='', begin=0.3):
    half = w / 2
    anim = f'<animate attributeName="stroke-dashoffset" from="1" to="0" dur="1.4s" begin="{begin}s" fill="freeze"/>' if draw else ''
    da = 'stroke-dasharray="1" stroke-dashoffset="1"' if draw else ''
    return f'''<g transform="translate({cx} {cy})" fill="none" stroke="{color}" stroke-width="1.6" stroke-linecap="round">
  <path pathLength="1" {da} d="M{-half} 0H-60M60 0H{half}">{anim}</path>
  <path pathLength="1" {da} d="M-60 0C-50 -14 -34 -14 -30 -2C-32 6 -42 8 -46 2M60 0C50 -14 34 -14 30 -2C32 6 42 8 46 2M-60 0C-50 14 -34 14 -30 2M60 0C50 14 34 14 30 2">{anim}</path>
  <path d="M0 -12L8 0L0 12L-8 0Z" fill="{accent}" stroke="none"><animate attributeName="opacity" from="0" to="1" dur=".4s" begin="{begin + 0.9}s" fill="freeze"/></path>
  <circle cx="-15" cy="0" r="2.2" fill="{color}" stroke="none"/><circle cx="15" cy="0" r="2.2" fill="{color}" stroke="none"/>
  <circle cx="{-half}" cy="0" r="2.6" fill="{color}" stroke="none"/><circle cx="{half}" cy="0" r="2.6" fill="{color}" stroke="none"/>
</g>'''

def knot_frame(w, h, uid, inset=0, band=18):
    return f'''<rect x="{inset}" y="{inset}" width="{w - 2 * inset}" height="{band}" fill="url(#knotH{uid})" stroke="{OR_D}"/>
<rect x="{inset}" y="{h - inset - band}" width="{w - 2 * inset}" height="{band}" fill="url(#knotH{uid})" stroke="{OR_D}"/>'''

def boss(x, y):
    return f'<g transform="translate({x} {y}) rotate(45)"><rect x="-15" y="-15" width="30" height="30" fill="{OR}" stroke="#5A1A12" stroke-width="1.5"/><circle r="6" fill="{GULES}"/></g>'

def write(name, content):
    with open(os.path.join(OUT, name), 'w', encoding='utf-8') as f:
        f.write(content)
    assert '—' not in content and '–' not in content, name
    print(name, len(content) // 1024, 'KB')

# ======================= hero =======================
W, H = 1280, 460
tag = 'Live chat translation for Age of Empires and Age of Mythology'
sub = 'Read what they write. Answer in their tongue.'
body = defs('h') + f'''
<rect width="{W}" height="{H}" fill="url(#oakh)"/>
<rect width="{W}" height="{H}" fill="url(#damh)"/>
{torch(150, 250, 'h', 0, seed=4)}
{torch(1130, 250, 'h', -1.3, seed=9)}
<rect width="{W}" height="{H}" fill="url(#vigh)"/>
{embers(14, [150, 1130], 170, 200, 'h')}
{knot_frame(W, H, 'h')}
{boss(9, 9)}{boss(W - 9, 9)}{boss(9, H - 9)}{boss(W - 9, H - 9)}
<linearGradient id="goldh" gradientUnits="userSpaceOnUse" x1="300" y1="0" x2="980" y2="0">
  <stop offset="0" stop-color="#8a6420"/><stop offset=".3" stop-color="{OR_L}"/><stop offset=".42" stop-color="#FFF4C8"/><stop offset=".54" stop-color="{OR_L}"/><stop offset="1" stop-color="#8a6420"/>
  <animateTransform attributeName="gradientTransform" type="translate" values="-700 0;700 0" dur="5s" repeatCount="indefinite"/>
</linearGradient>
<g opacity="0">
  <animate attributeName="opacity" from="0" to="1" dur=".9s" begin=".2s" fill="freeze"/>
  <text x="640" y="232" text-anchor="middle" class="frak" font-size="196" fill="url(#goldh)" stroke="#3a2410" stroke-width="1.2" paint-order="stroke">Envoy</text>
</g>
{fleuron(640, 272, 440, uid='h', begin=.8)}
<text x="640" y="326" text-anchor="middle" class="sc" font-size="34" fill="#F1E0B9" opacity="0">{tag}<animate attributeName="opacity" from="0" to="1" dur=".7s" begin="1.4s" fill="freeze"/></text>
<text x="640" y="368" text-anchor="middle" class="it" font-size="25" fill="{LEATHER}" opacity="0">{sub}<animate attributeName="opacity" from="0" to="1" dur=".7s" begin="1.9s" fill="freeze"/></text>
'''
write('hero.svg', svg(W, H, body, ['Envoy', tag, sub], 'Envoy: live chat translation for Age of Empires and Age of Mythology'))

# ======================= buttons =======================
def button(name, label, sub, wax_glyph, w=330, primary=False):
    h = 84
    bg = f'url(#waxRb)' if primary else OAK
    fg = '#F8E3C9' if primary else '#F1E0B9'
    body = defs('b') + f'''
<linearGradient id="shine" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#fff" stop-opacity="0"/><stop offset=".5" stop-color="#fff" stop-opacity=".28"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>
<clipPath id="cl"><rect x="4" y="4" width="{w - 8}" height="{h - 8}" rx="6"/></clipPath>
<rect x="4" y="4" width="{w - 8}" height="{h - 8}" rx="6" fill="{bg}" stroke="{OR}" stroke-width="2.5"/>
<rect x="10" y="10" width="{w - 20}" height="{h - 20}" rx="3" fill="none" stroke="{OR_L}" stroke-opacity=".55" stroke-dasharray="4 4"/>
<g clip-path="url(#cl)"><rect x="-120" y="0" width="90" height="{h}" fill="url(#shine)" transform="skewX(-20)"><animate attributeName="x" values="-160;{w + 80};{w + 80}" keyTimes="0;.35;1" dur="4s" repeatCount="indefinite"/></rect></g>
<g transform="translate(46 {h / 2})"><circle r="25" fill="{'#F3D98C' if primary else 'url(#waxRb)'}" stroke="{OR_DD}" stroke-width="1.5"/><text y="9" text-anchor="middle" class="frak" font-size="28" fill="{GULES if primary else '#F8E3C9'}">{wax_glyph}</text></g>
<text x="86" y="{h / 2 - 2}" class="sc" font-size="27" fill="{fg}">{label}</text>
<text x="86" y="{h / 2 + 22}" class="it" font-size="17" fill="{fg}" fill-opacity=".85">{sub}</text>
'''
    write(name, svg(w, h, body, [label, sub, wax_glyph], label))

button('btn-download.svg', 'Download', 'Windows 10 and 11, free', 'E', primary=True)
button('btn-setup.svg', 'Set up in 2 minutes', 'the six-step guide', 'S')
button('btn-use.svg', 'How to use it', 'hotkeys and replies', 'U')

# ======================= how it works =======================
W, H = 1280, 520
CYC = 12
def kt(*pairs):
    """Opacity track over the 12 s loop: pairs of (time, value)."""
    ts = ';'.join(f'{t / CYC:.4f}' for t, _ in pairs)
    vs = ';'.join(str(v) for _, v in pairs)
    return f'<animate attributeName="opacity" values="{vs}" keyTimes="{ts}" dur="{CYC}s" repeatCount="indefinite"/>'

def panel(x, title, n):
    return f'''<g transform="translate({x} 0)">
  <rect x="0" y="70" width="380" height="400" rx="4" fill="url(#velw)" stroke="{OR_D}" stroke-width="2"/>
  <rect x="9" y="79" width="362" height="382" fill="none" stroke="{GULES}" stroke-opacity=".55" stroke-width="1.5"/>
  <g transform="translate(36 40)"><circle r="26" fill="url(#waxRw)" stroke="#4A0F08" stroke-width="1.5"/><text y="10" text-anchor="middle" class="sc" font-size="27" fill="#F8E3C9">{n}</text></g>
  <text x="74" y="50" class="sc" font-size="30" fill="#F1E0B9">{title}</text>
</g>'''

chat_lines = [('[All] ', '龙骑士', '#e2574c', ': 你好，再来一局吗？'), ('[All] ', '전략가', '#5aa0e6', ': ㅈㅈ'), ('[Team] ', 'Сокол', '#8fd18a', ': хорошая стена')]
chat = ''
for i, (ch, who, col, txt) in enumerate(chat_lines):
    t0 = 0.4 + i * 0.6
    chat += f'<g opacity="0">{kt((0, 0), (t0, 0), (t0 + .25, 1), (11.4, 1), (11.8, 0), (12, 0))}<text x="22" y="{360 + i * 30}" class="cjk" font-size="19" fill="#f4ecdc"><tspan fill="#f2c94c">{ch}</tspan><tspan fill="{col}">{who}</tspan>{txt}</text></g>'

scroll_y = 120
body = defs('w') + f'''
<rect width="{W}" height="{H}" fill="url(#oakw)"/><rect width="{W}" height="{H}" fill="url(#damw)"/><rect width="{W}" height="{H}" fill="url(#vigw)"/>
{panel(30, 'They write', '1')}{panel(450, 'Envoy translates', '2')}{panel(870, 'You answer', '3')}
<g transform="translate(30 0)">
  <rect x="18" y="300" width="344" height="150" rx="3" fill="#20271b" stroke="#4A3020" stroke-width="2"/>
  <text x="30" y="324" class="it" font-size="14" fill="#f0e1be" fill-opacity=".55">the game chat, on your screen</text>
  {chat}
  <g transform="translate(190 190)">
    <circle r="62" fill="none" stroke="{INK_S}" stroke-width="2" stroke-dasharray="6 6"><animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="18s" repeatCount="indefinite"/></circle>
    <text y="-6" text-anchor="middle" class="sc" font-size="20" fill="{INK}">Chinese</text><text y="18" text-anchor="middle" class="sc" font-size="20" fill="{INK}">Korean</text><text y="42" text-anchor="middle" class="sc" font-size="20" fill="{INK}">Russian</text>
    <text y="-30" text-anchor="middle" class="it" font-size="15" fill="{INK_S}">and more</text>
  </g>
</g>
<g transform="translate(450 0)">
  <rect x="20" y="300" width="340" height="150" rx="3" fill="none" stroke="{OR_D}" stroke-width="3" stroke-dasharray="12 7" opacity="0">{kt((0, 0), (2.2, 0), (2.5, 1), (11.4, 1), (11.8, 0), (12, 0))}</rect>
  <text x="190" y="380" text-anchor="middle" class="it" font-size="17" fill="{INK_S}" opacity="0">{kt((0, 0), (2.4, 0), (2.7, 1), (11.4, 1), (11.8, 0), (12, 0))}Envoy reads the chat from your screen</text>
  <g opacity="0">{kt((0, 0), (3.0, 0), (3.3, 1), (11.4, 1), (11.8, 0), (12, 0))}
    <rect x="22" y="{scroll_y}" width="336" height="10" rx="5" fill="{OR_D}"/>
    <rect x="28" y="{scroll_y + 10}" width="324" height="110" fill="url(#velw)" stroke="#C4A673"/>
    <rect x="22" y="{scroll_y + 120}" width="336" height="10" rx="5" fill="{OR_D}"/>
    <g transform="translate(66 {scroll_y + 54})" opacity="0">{kt((0, 0), (3.6, 0), (3.75, 1), (11.4, 1), (11.8, 0), (12, 0))}<circle r="25" fill="url(#waxRw)"/><text y="9" text-anchor="middle" class="cjk" font-size="22" fill="#F8E3C9">中</text></g>
    <text x="104" y="{scroll_y + 40}" class="sc" font-size="15" fill="{GULES}">龙骑士 · All · Chinese</text>
    <text x="104" y="{scroll_y + 72}" class="ga" font-size="25" fill="{INK}">Hello! Another round?</text>
    <text x="104" y="{scroll_y + 100}" class="it" font-size="16" fill="{INK_S}">你好，再来一局吗？</text>
  </g>
</g>
<g transform="translate(870 0)">
  <g opacity="0">{kt((0, 0), (5.2, 0), (5.5, 1), (11.4, 1), (11.8, 0), (12, 0))}
    <g transform="translate(40 108)">{''.join(f'<g transform="translate({i * 104} 0)"><rect width="92" height="64" rx="7" fill="#E7D4A8" stroke="#8E6A3C" stroke-width="2"/><text x="46" y="42" text-anchor="middle" class="sc" font-size="{26 if k != "⇧" else 30}" fill="{INK}" font-family="FellSC, Segoe UI Symbol, Apple Symbols, sans-serif">{k}</text></g>' for i, k in enumerate(['Ctrl', '⇧', 'T']))}</g>
  </g>
  <g opacity="0">{kt((0, 0), (6.2, 0), (6.5, 1), (11.4, 1), (11.8, 0), (12, 0))}
    <text x="40" y="226" class="it" font-size="16" fill="{INK_S}">Your words</text>
    <text x="40" y="258" class="ga" font-size="26" fill="{INK}">gg wp, rematch?</text>
    <line x1="40" y1="268" x2="340" y2="268" stroke="#8E6A3C" stroke-width="2"/>
  </g>
  <g opacity="0">{kt((0, 0), (7.4, 0), (7.6, 1), (11.4, 1), (11.8, 0), (12, 0))}
    <text x="40" y="304" class="sc" font-size="15" fill="{GULES}">The scribe writes</text>
    <clipPath id="ink"><rect x="40" y="310" width="0" height="50"><animate attributeName="width" values="0;0;320;320;0" keyTimes="0;{7.5 / CYC:.4f};{9.0 / CYC:.4f};{11.6 / CYC:.4f};1" dur="{CYC}s" repeatCount="indefinite"/></rect></clipPath>
    <text x="40" y="344" class="cjk" font-size="23" fill="{INK}" clip-path="url(#ink)">좋은 게임이었어요! 한 판 더?</text>
  </g>
  <g transform="translate(286 410)" opacity="0">{kt((0, 0), (9.4, 0), (9.6, 1), (11.4, 1), (11.8, 0), (12, 0))}<circle r="38" fill="url(#waxRw)" stroke="#4A0F08" stroke-width="2"/><text y="-3" text-anchor="middle" class="sc" font-size="15" fill="#F8E3C9">Seal &amp;</text><text y="16" text-anchor="middle" class="sc" font-size="15" fill="#F8E3C9">send</text></g>
  <text x="40" y="420" class="it" font-size="17" fill="#3F6B3A" opacity="0">{kt((0, 0), (10.0, 0), (10.3, 1), (11.4, 1), (11.8, 0), (12, 0))}Typed into game chat for you.</text>
</g>
'''
write('how.svg', svg(W, H, body, ['They write', 'Envoy translates', 'You answer', 'Chinese', 'Korean', 'Russian', 'and more', 'the game chat, on your screen',
                                  'Envoy reads the chat from your screen', 'All Chinese', 'Hello! Another round?', 'Ctrl', 'T', 'Your words', 'gg wp, rematch?',
                                  'The scribe writes', 'Seal & send', 'Typed into game chat for you.', '123'], 'How Envoy works: they write, Envoy translates, you answer'))

# ======================= setup: six seals =======================
W, H = 1280, 250
steps = [('Language', 'and privacy'), ('Game', 'borderless window'), ('Chat area', 'drag a box'), ('Text reading', 'language packs'), ('Test', 'one translation'), ('Hotkeys', 'check the keys')]
CYC = 9
body = defs('s') + f'<rect width="{W}" height="{H}" rx="6" fill="url(#vels)"/><rect x="10" y="10" width="{W - 20}" height="{H - 20}" fill="none" stroke="{GULES}" stroke-opacity=".6" stroke-width="2"/>'
body += f'<text x="640" y="54" text-anchor="middle" class="sc" font-size="30" fill="{INK}">The setup guide walks you through six steps</text>'
x0, dx = 140, 200
body += f'<line x1="{x0}" y1="130" x2="{x0 + 5 * dx}" y2="130" stroke="#BFA77A" stroke-width="4"/>'
body += f'<line x1="{x0}" y1="130" x2="{x0}" y2="130" stroke="{GULES}" stroke-width="4"><animate attributeName="x2" values="{x0};{x0 + 5 * dx};{x0 + 5 * dx};{x0}" keyTimes="0;{6.2 / CYC:.3f};{8.6 / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/></line>'
for i, (a, b) in enumerate(steps):
    x = x0 + i * dx
    t = 0.3 + i * 1.0
    body += f'<g transform="translate({x} 130)"><circle r="30" fill="url(#vels)" stroke="#BFA77A" stroke-width="2"/>'
    body += f'<g opacity="0"><animate attributeName="opacity" values="0;0;1;1;0" keyTimes="0;{t / CYC:.3f};{(t + .15) / CYC:.3f};{8.6 / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/>'
    body += f'<g><animateTransform attributeName="transform" type="scale" values="2;2;.9;1;1" keyTimes="0;{t / CYC:.3f};{(t + .2) / CYC:.3f};{(t + .3) / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/><circle r="30" fill="url(#waxRs)" stroke="#4A0F08" stroke-width="1.5"/></g></g>'
    body += f'<text y="10" text-anchor="middle" class="sc" font-size="27" fill="#F8E3C9" stroke="#6E1A10" stroke-width=".6">{i + 1}</text></g>'
    body += f'<text x="{x}" y="196" text-anchor="middle" class="sc" font-size="22" fill="{INK}">{a}</text><text x="{x}" y="220" text-anchor="middle" class="it" font-size="17" fill="{INK_S}">{b}</text>'
write('setup.svg', svg(W, H, body, ['The setup guide walks you through six steps'] + [a + b for a, b in steps], 'The setup guide: six steps'))

# ======================= features =======================
W, H = 1280, 600
FEAT = [
    ('Free to use', 'Google and Microsoft need no account. Paid translators use your own key.', '<circle cx="40" cy="44" r="24"/><circle cx="40" cy="44" r="15"/><path d="M40 34v20M33 40h14"/>'),
    ('Reads your screen', 'It reads chat the way OBS sees it and types your reply for you.', '<path d="M8 44c14-22 50-22 64 0c-14 22-50 22-64 0z"/><circle cx="40" cy="44" r="11"/>'),
    ('Every Age', 'Age of Empires I, II, III, IV and Age of Mythology.', '<path d="M14 16h52v28c0 20-14 30-26 36c-12-6-26-16-26-36z"/><path d="M14 16l52 52"/>'),
    ('Gamer slang', '666, ㅈㅈ, изи and kkkk, explained offline.', '<path d="M16 22h40c6 0 8 6 8 10v32c0 4-2 8-8 8H24"/><path d="M16 22c-6 0-8 6-8 10s2 8 8 8h8V30c0-4-2-8-8-8z"/><path d="M30 42h22M30 52h22M30 62h14"/>'),
    ('Phrasebook', 'gl hf, gg wp, rematch? Ready in 13 languages.', '<path d="M12 20c12-6 22-6 28 2c6-8 16-8 28-2v46c-12-6-22-6-28 2c-6-8-16-8-28-2z"/><path d="M40 22v46"/>'),
    ('Private', 'No telemetry. Keys are encrypted on your PC.', '<rect x="18" y="38" width="44" height="34" rx="4"/><path d="M28 38V28a12 12 0 0 1 24 0v10"/><circle cx="40" cy="55" r="4"/>'),
]
CYC = 24
body = defs('f') + f'<rect width="{W}" height="{H}" fill="url(#oakf)"/><rect width="{W}" height="{H}" fill="url(#damf)"/><rect width="{W}" height="{H}" fill="url(#vigf)"/>'
for i, (title, line, icon) in enumerate(FEAT):
    cx, cy = 40 + (i % 3) * 410, 40 + (i // 3) * 280
    t = 0.2 + i * 0.35
    body += f'<g transform="translate({cx} {cy})" opacity="0"><animate attributeName="opacity" values="0;0;1;1;0" keyTimes="0;{t / CYC:.3f};{(t + .35) / CYC:.3f};{(CYC - .7) / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/>'
    body += f'<rect width="380" height="250" rx="4" fill="url(#velf)" stroke="{OR}" stroke-width="3"/><rect x="8" y="8" width="364" height="234" fill="none" stroke="{GULES}" stroke-opacity=".55" stroke-width="1.5"/>'
    body += f'<g transform="translate(26 30)" fill="none" stroke="{INK}" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round">'
    import re
    anim = f'<animate attributeName="stroke-dashoffset" values="1;1;0;0;1" keyTimes="0;{(t + .2) / CYC:.3f};{(t + 1.6) / CYC:.3f};{(CYC - .7) / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/>'
    body += re.sub(r'<(path|circle|rect)([^>]*?)/>', lambda m: f'<{m.group(1)}{m.group(2)} pathLength="1" stroke-dasharray="1" stroke-dashoffset="1">{anim}</{m.group(1)}>', icon)
    body += '</g>'
    body += f'<text x="120" y="74" class="frak" font-size="40" fill="{GULES}">{title[0]}</text><text x="146" y="74" class="sc" font-size="32" fill="{INK}">{title[1:]}</text>'
    words, lines, cur = line.split(' '), [], ''
    for wd in words:
        if len(cur) + len(wd) > 30: lines.append(cur.strip()); cur = ''
        cur += wd + ' '
    lines.append(cur.strip())
    for j, ln in enumerate(lines):
        body += f'<text x="30" y="{150 + j * 32}" class="cjk" font-size="23" fill="#4a3824">{ln}</text>'
    body += '</g>'
write('features.svg', svg(W, H, body, [f[0] + f[1] for f in FEAT], 'What Envoy does'))

# ======================= liveries =======================
W, H = 1280, 330
LIV = [('Age of Empires', 'sable and or', 'linear', '#1C1A18', '#D8B04A'), ('Age of Empires II', 'azure and or', 'v', '#2B4C8C', '#D8B04A'),
       ('Age of Empires III', 'vert and argent', 'h', '#2F5E35', '#E9E4D6'), ('Age of Empires IV', 'gules and or', 'd', '#8E2418', '#D8B04A'),
       ('Age of Mythology', 'purpure and or', 'c', '#5B2D73', '#D8B04A')]
CYC = 10
body = defs('l') + f'<rect width="{W}" height="{H}" fill="url(#oakl)"/><rect width="{W}" height="{H}" fill="url(#daml)"/><rect width="{W}" height="{H}" fill="url(#vigl)"/>'
body += f'<text x="640" y="56" text-anchor="middle" class="sc" font-size="32" fill="#F1E0B9">A livery for every Age. Envoy follows the game you are playing.</text>'
shield = 'M0 0H96V56C96 88 70 104 48 116C26 104 0 88 0 56Z'
for i, (name, blazon, kind, a, b) in enumerate(LIV):
    x = 120 + i * 230
    t = i * 2.0
    clip = f'<clipPath id="sh{i}"><path d="{shield}"/></clipPath>'
    if kind == 'v': div = f'<rect width="48" height="120" fill="{a}"/><rect x="48" width="48" height="120" fill="{b}"/>'
    elif kind == 'h': div = f'<rect width="96" height="58" fill="{a}"/><rect y="58" width="96" height="62" fill="{b}"/>'
    elif kind == 'd': div = f'<rect width="96" height="120" fill="{b}"/><path d="M0 0H96L0 120Z" fill="{a}"/>'
    elif kind == 'c': div = f'<rect width="96" height="120" fill="{a}"/><path d="M0 92L48 44L96 92V74L48 26L0 74Z" fill="{b}"/>'
    else: div = f'<rect width="96" height="120" fill="{a}"/><path d="M48 12L58 40L88 40L64 58L72 88L48 70L24 88L32 58L8 40L38 40Z" fill="{b}"/>'
    body += f'<g transform="translate({x - 48} 100)">{clip}<g><animateTransform attributeName="transform" type="translate" values="0 0;0 0;0 -16;0 -16;0 0;0 0" keyTimes="0;{t / CYC:.3f};{(t + .25) / CYC:.3f};{(t + 1.8) / CYC:.3f};{(t + 2.0) / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/>'
    body += f'<g clip-path="url(#sh{i})">{div}</g><path d="{shield}" fill="none" stroke="{OR_L}" stroke-width="3"/>'
    body += f'<path d="{shield}" fill="none" stroke="#FFE7A0" stroke-width="7" opacity="0"><animate attributeName="opacity" values="0;0;.9;.9;0;0" keyTimes="0;{t / CYC:.3f};{(t + .25) / CYC:.3f};{(t + 1.8) / CYC:.3f};{(t + 2.0) / CYC:.3f};1" dur="{CYC}s" repeatCount="indefinite"/></path></g></g>'
    body += f'<text x="{x}" y="262" text-anchor="middle" class="sc" font-size="21" fill="#F1E0B9">{name}</text><text x="{x}" y="288" text-anchor="middle" class="it" font-size="17" fill="{LEATHER}">{blazon}</text>'
write('liveries.svg', svg(W, H, body, ['A livery for every Age. Envoy follows the game you are playing.'] + [l[0] + l[1] for l in LIV], 'A livery for every Age game'))

# ======================= divider and section headers =======================
def header(name, title, glyph):
    W, H = 1280, 120
    body = defs('d') + fleuron(640, 92, 900, color=OR_D, uid='d', begin=0.1)
    body += f'<g transform="translate(640 46)"><text text-anchor="middle" y="14" class="sc" font-size="40" fill="{OR_D}" stroke="#2A1D12" stroke-opacity=".0">{title}</text></g>'
    write(name, svg(W, H, body, [title], title))

for fname, title in [('h-download.svg', 'Download'), ('h-setup.svg', 'Set up'), ('h-use.svg', 'Use it in a match'), ('h-translators.svg', 'Translators and cost'),
                     ('h-safe.svg', 'Safe for ranked play'), ('h-faq.svg', 'Questions'), ('h-build.svg', 'Build it yourself')]:
    header(fname, title, title[0])
print('done')
