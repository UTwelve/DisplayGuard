# -*- coding: utf-8 -*-
"""DisplayGuard icon v2: Edge-style flat gradient. One screen + a window floating outside it."""
import numpy as np
from PIL import Image, ImageDraw

S = 1024

# diagonal gradient: teal -> blue (Edge-like)
c1 = np.array([46, 199, 178], dtype=float)   # teal
c2 = np.array([43, 111, 210], dtype=float)   # blue
yy, xx = np.mgrid[0:S, 0:S]
t = ((xx + yy) / (2.0 * (S - 1)))[..., None]
grad = (c1 * (1 - t) + c2 * t).astype(np.uint8)
bg = Image.fromarray(np.dstack([grad, np.full((S, S), 255, np.uint8)]), "RGBA")

# rounded-square mask
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([8, 8, S - 8, S - 8], radius=200, fill=255)
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
img.paste(bg, (0, 0), mask)

d = ImageDraw.Draw(img)
WHITE = (255, 255, 255, 255)
SOFT  = (255, 255, 255, 90)

# monitor: white outline, left-center
mx0, my0, mx1, my1 = 130, 300, 660, 660
bw = 46
d.rounded_rectangle([mx0, my0, mx1, my1], radius=54, outline=WHITE, width=bw)
# stand
d.rectangle([mx0 + 218, my1 + 6, mx0 + 310, my1 + 108], fill=WHITE)
d.rounded_rectangle([mx0 + 110, my1 + 96, mx1 - 110, my1 + 148], radius=26, fill=WHITE)

# floating window: straddling the monitor's top-right corner, sticking OUT of the screen
wx0, wy0, wx1, wy1 = 560, 170, 870, 420
d.rounded_rectangle([wx0, wy0, wx1, wy1], radius=40, fill=WHITE)
# title bar strip (translucent teal tint for depth, still flat)
d.rounded_rectangle([wx0, wy0, wx1, wy1], radius=40, fill=(255, 255, 255, 0))
d.rectangle([wx0 + 4, wy0 + 4, wx1 - 4, wy0 + 62], fill=(230, 244, 250, 255))
# close dot
d.ellipse([wx1 - 56, wy0 + 18, wx1 - 26, wy0 + 48], fill=(120, 200, 220, 255))

base = img.resize((256, 256), Image.LANCZOS)
base.save("DisplayGuard.png")
base.save("DisplayGuard.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])

sizes = [16, 24, 32, 48, 64, 256]
pad = 16
W = sum(sizes) + pad * (len(sizes) + 1)
strip = Image.new("RGB", (W, 256 + pad * 2), (200, 200, 200))
x = pad
for s in sizes:
    r = img.resize((s, s), Image.LANCZOS)
    strip.paste(r, (x, pad + 256 - s), r)
    x += s + pad
strip.save("preview.png")
print("done")
