"""A particle flame for SVG: soft blobs rise, shrink and cool from white-gold to deep red, merged by a goo filter.
No uniform pulsing and no per-frame randomness: every blob has its own duration, so the pattern never visibly loops."""
import random

def flame_defs(uid):
    return f'''<filter id="goo{uid}" x="-150%" y="-150%" width="400%" height="400%" color-interpolation-filters="sRGB">
  <feGaussianBlur in="SourceGraphic" stdDeviation="4.2" result="b"/>
  <feColorMatrix in="b" type="matrix" values="1 0 0 0 0  0 1 0 0 0  0 0 1 0 0  0 0 0 19 -7" result="g"/>
  <feGaussianBlur in="g" stdDeviation="0.9"/>
</filter>
<filter id="haze{uid}" x="-200%" y="-200%" width="500%" height="500%"><feGaussianBlur stdDeviation="9"/></filter>
<radialGradient id="tglow{uid}"><stop offset="0" stop-color="#FFB25A" stop-opacity=".5"/><stop offset=".35" stop-color="#E0782E" stop-opacity=".2"/><stop offset=".7" stop-color="#7A3210" stop-opacity=".06"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>'''

def flame(uid, seed=1, scale=1.0, light=230):
    r = random.Random(seed)
    blobs = []
    n = 16
    for i in range(n):
        dur = r.uniform(0.62, 1.12)
        beg = -r.uniform(0, dur)
        x0 = r.uniform(-7, 7)
        rise = r.uniform(58, 92)
        drift = r.uniform(-9, 9)
        r0 = r.uniform(8.5, 12.5)
        mid = x0 + drift * 0.4 + r.uniform(-4, 4)
        blobs.append(
            f'<circle r="{r0:.1f}" cx="{x0:.1f}" cy="0" fill="#FFF3C4">'
            f'<animate attributeName="cy" values="2;{-rise * 0.45:.1f};{-rise:.1f}" keyTimes="0;.45;1" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite" calcMode="spline" keySplines=".3 0 .7 1;.3 0 .7 1"/>'
            f'<animate attributeName="cx" values="{x0:.1f};{mid:.1f};{x0 + drift:.1f}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
            f'<animate attributeName="r" values="{r0:.1f};{r0 * 0.75:.1f};1.2" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
            f'<animate attributeName="fill" values="#FFF6D6;#FFD27A;#F7962F;#D9471B;#8E1C08" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
            '</circle>')
    smoke = []
    for i in range(5):
        dur = r.uniform(2.6, 3.8); beg = -r.uniform(0, dur); x0 = r.uniform(-5, 5)
        smoke.append(f'<circle r="6" cx="{x0:.1f}" cy="-70" fill="#5a4e46"><animate attributeName="cy" values="-70;-150" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
                     f'<animate attributeName="cx" values="{x0:.1f};{x0 + r.uniform(-18, 18):.1f}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
                     f'<animate attributeName="r" values="5;16" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
                     f'<animate attributeName="opacity" values="0;.22;0" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/></circle>')
    sparks = []
    for i in range(4):
        dur = r.uniform(1.4, 2.6); beg = -r.uniform(0, 4); x0 = r.uniform(-6, 6)
        sparks.append(f'<circle r="1.3" cx="{x0:.1f}" cy="-40" fill="#FFE2A0"><animate attributeName="cy" values="-40;{-150 - r.uniform(0, 60):.0f}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
                      f'<animate attributeName="cx" values="{x0:.1f};{x0 + r.uniform(-30, 30):.1f}" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/>'
                      f'<animate attributeName="opacity" values="0;1;1;0" keyTimes="0;.1;.6;1" dur="{dur:.2f}s" begin="{beg:.2f}s" repeatCount="indefinite"/></circle>')
    # light: three nested layers at unrelated periods multiply into an irregular, gentle flicker
    glow = (f'<g opacity=".93"><animate attributeName="opacity" values=".93;.84;.97;.88;.93" dur="0.83s" repeatCount="indefinite"/>'
            f'<g opacity=".95"><animate attributeName="opacity" values=".95;1;.86;.98;.92;.95" dur="1.37s" repeatCount="indefinite"/>'
            f'<circle r="{light}" cy="-40" fill="url(#tglow{uid})"><animate attributeName="opacity" values="1;.9;1;.94;1" dur="3.1s" repeatCount="indefinite"/></circle></g></g>')
    core = (f'<ellipse cx="0" cy="-2" rx="11" ry="9" fill="#FFF8DE"><animate attributeName="rx" values="11;10;12;10.5;11" dur="0.9s" repeatCount="indefinite"/></ellipse>')
    sway = '<animateTransform attributeName="transform" type="skewX" values="0;-3;1.5;-1;2.5;0" dur="2.9s" repeatCount="indefinite"/>'
    return f'''<g transform="scale({scale})">{glow}
  <g opacity=".75" filter="url(#haze{uid})"><ellipse cx="0" cy="-28" rx="18" ry="34" fill="#FF9A3C"><animate attributeName="ry" values="34;30;37;31;34" dur="1.1s" repeatCount="indefinite"/></ellipse></g>
  <g>{sway}<g filter="url(#goo{uid})">{''.join(blobs)}{core}</g></g>
  {''.join(smoke)}{''.join(sparks)}
</g>'''
