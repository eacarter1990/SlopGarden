#!/usr/bin/env python3
"""Renders the stylized top-down garden-bed and NFT-rack JPEGs bundled with the sensor emulator.

Usage: pip install pillow && python3 scripts/generate-emulator-images.py
"""
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

W, H = 1280, 720
SS = 2  # supersampling factor for smooth edges
OUT = Path(__file__).resolve().parent.parent / "src" / "SlopGarden.Emulator" / "Assets"


def jitter(rng, c, amount):
    return tuple(max(0, min(255, v + rng.randint(-amount, amount))) for v in c)


def shade(rng, c, amount):
    k = rng.randint(-amount, amount)
    return tuple(max(0, min(255, v + k)) for v in c)


def ground(rng, img):
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W * SS, H * SS], fill=(92, 120, 70))
    for _ in range(26000):
        x, y = rng.randrange(W * SS), rng.randrange(H * SS)
        r = rng.randint(2, 7)
        d.ellipse([x - r, y - r, x + r, y + r], fill=shade(rng, (98, 128, 74), 16))
    # gravel path along the bottom
    d.rectangle([0, int(H * SS * 0.88), W * SS, H * SS], fill=(168, 160, 146))
    for _ in range(9000):
        x = rng.randrange(W * SS)
        y = rng.randrange(int(H * SS * 0.88), H * SS)
        r = rng.randint(3, 9)
        d.ellipse([x - r, y - r, x + r, y + r], fill=shade(rng, (176, 168, 152), 24))


def raised_bed(rng, img, box):
    d = ImageDraw.Draw(img)
    x0, y0, x1, y1 = box
    t = 46 * SS
    # drop shadow
    shadow = Image.new("L", img.size, 0)
    ImageDraw.Draw(shadow).rounded_rectangle([x0 + 18 * SS, y0 + 22 * SS, x1 + 18 * SS, y1 + 22 * SS], 18 * SS, fill=150)
    shadow = shadow.filter(ImageFilter.GaussianBlur(22 * SS))
    img.paste((30, 40, 25), (0, 0), shadow)
    d = ImageDraw.Draw(img)
    # timber frame
    wood = (150, 104, 64)
    d.rounded_rectangle(box, 10 * SS, fill=wood)
    for i in range(0, x1 - x0, 6 * SS):
        c = jitter(rng, wood, 14)
        d.line([x0 + i, y0, x0 + i + rng.randint(-8, 8), y0 + t], fill=c, width=3 * SS)
        d.line([x0 + i, y1 - t, x0 + i + rng.randint(-8, 8), y1], fill=c, width=3 * SS)
    for j in range(0, y1 - y0, 6 * SS):
        c = jitter(rng, wood, 14)
        d.line([x0, y0 + j, x0 + t, y0 + j + rng.randint(-8, 8)], fill=c, width=3 * SS)
        d.line([x1 - t, y0 + j, x1, y0 + j + rng.randint(-8, 8)], fill=c, width=3 * SS)
    for cx, cy in [(x0, y0), (x1 - t, y0), (x0, y1 - t), (x1 - t, y1 - t)]:
        d.rectangle([cx, cy, cx + t, cy + t], fill=(122, 82, 48))
        d.ellipse([cx + t / 2 - 5 * SS, cy + t / 2 - 5 * SS, cx + t / 2 + 5 * SS, cy + t / 2 + 5 * SS], fill=(80, 80, 84))
    # soil
    s = [x0 + t, y0 + t, x1 - t, y1 - t]
    d.rectangle(s, fill=(74, 52, 36))
    for _ in range(30000):
        x = rng.randrange(s[0], s[2])
        y = rng.randrange(s[1], s[3])
        r = rng.randint(1, 5)
        d.ellipse([x - r, y - r, x + r, y + r], fill=jitter(rng, (78, 55, 38), 18))
    return s


def drip_line(d, soil, rows):
    for y in rows:
        d.line([soil[0], y + 34 * SS, soil[2], y + 34 * SS], fill=(28, 28, 30), width=6 * SS)
        for x in range(soil[0] + 40 * SS, soil[2], 120 * SS):
            d.ellipse([x - 5 * SS, y + 29 * SS, x + 5 * SS, y + 39 * SS], fill=(60, 60, 64))


def leaf_cluster(rng, d, cx, cy, radius, base, n):
    for _ in range(n):
        a = rng.uniform(0, math.tau)
        dist = rng.uniform(0, radius * 0.75)
        x, y = cx + math.cos(a) * dist, cy + math.sin(a) * dist
        r = rng.uniform(radius * 0.22, radius * 0.4)
        d.ellipse([x - r, y - r * 0.8, x + r, y + r * 0.8], fill=jitter(rng, base, 22))


def tomato_plant(rng, d, cx, cy):
    r = 92 * SS
    leaf_cluster(rng, d, cx, cy, r, (52, 118, 48), 70)
    leaf_cluster(rng, d, cx, cy, r * 0.7, (74, 148, 60), 40)
    d.ellipse([cx - 9 * SS, cy - 9 * SS, cx + 9 * SS, cy + 9 * SS], fill=(196, 170, 120))  # stake top
    for _ in range(rng.randint(6, 10)):
        a = rng.uniform(0, math.tau)
        dist = rng.uniform(r * 0.25, r * 0.85)
        x, y = cx + math.cos(a) * dist, cy + math.sin(a) * dist
        tr = rng.uniform(13, 19) * SS
        ripe = rng.random() < 0.7
        col = (214, 44, 36) if ripe else (232, 150, 40) if rng.random() < 0.5 else (120, 170, 60)
        d.ellipse([x - tr, y - tr, x + tr, y + tr], fill=col)
        d.ellipse([x - tr * 0.45, y - tr * 0.65, x - tr * 0.05, y - tr * 0.25], fill=jitter(rng, (255, 220, 200), 10))
        d.polygon([(x, y - tr - 4 * SS), (x - 6 * SS, y - tr + 6 * SS), (x + 6 * SS, y - tr + 6 * SS)], fill=(40, 100, 40))


def lettuce_plant(rng, d, cx, cy, red):
    r = 58 * SS
    outer = (150, 70, 80) if red else (96, 168, 64)
    inner = (190, 110, 110) if red else (160, 214, 96)
    for i in range(14):
        a = i / 14 * math.tau + rng.uniform(-0.15, 0.15)
        x, y = cx + math.cos(a) * r * 0.55, cy + math.sin(a) * r * 0.55
        lr = r * rng.uniform(0.42, 0.52)
        d.ellipse([x - lr, y - lr, x + lr, y + lr], fill=jitter(rng, outer, 14))
    for i in range(9):
        a = i / 9 * math.tau + 0.3
        x, y = cx + math.cos(a) * r * 0.28, cy + math.sin(a) * r * 0.28
        lr = r * 0.32
        d.ellipse([x - lr, y - lr, x + lr, y + lr], fill=jitter(rng, inner, 12))
    d.ellipse([cx - r * 0.18, cy - r * 0.18, cx + r * 0.18, cy + r * 0.18], fill=(206, 236, 150) if not red else (220, 160, 150))


def basil_plant(rng, d, cx, cy):
    leaf_cluster(rng, d, cx, cy, 44 * SS, (58, 140, 58), 26)
    leaf_cluster(rng, d, cx, cy, 30 * SS, (90, 176, 78), 16)


def vignette(img):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).ellipse([-W * SS * 0.25, -H * SS * 0.35, W * SS * 1.25, H * SS * 1.35], fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(120 * SS))
    dark = Image.new("RGB", img.size, (12, 18, 10))
    return Image.composite(img, dark, mask)


def render(name, kind, seed):
    rng = random.Random(seed)
    img = Image.new("RGB", (W * SS, H * SS))
    ground(rng, img)
    box = [90 * SS, 60 * SS, (W - 90) * SS, int(H * 0.84) * SS]
    soil = raised_bed(rng, img, box)
    d = ImageDraw.Draw(img)
    if kind == "tomato":
        rows = [soil[1] + 130 * SS, soil[1] + 330 * SS]
        drip_line(d, soil, rows)
        for y in rows:
            for x in range(soil[0] + 120 * SS, soil[2] - 60 * SS, 230 * SS):
                tomato_plant(rng, d, x + rng.randint(-12, 12) * SS, y + rng.randint(-10, 10) * SS)
        for x in range(soil[0] + 70 * SS, soil[2] - 40 * SS, 150 * SS):
            basil_plant(rng, d, x, soil[3] - 40 * SS)
    else:
        rows = [soil[1] + 72 * SS + i * 116 * SS for i in range(4)]
        drip_line(d, soil, rows[::2])
        for ri, y in enumerate(rows):
            for ci, x in enumerate(range(soil[0] + 80 * SS, soil[2] - 40 * SS, 135 * SS)):
                lettuce_plant(rng, d, x + rng.randint(-8, 8) * SS, y + rng.randint(-8, 8) * SS, red=(ri + ci) % 3 == 0)
    img = vignette(img.filter(ImageFilter.GaussianBlur(0.6 * SS)))
    img = img.resize((W, H), Image.LANCZOS)
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / name, "JPEG", quality=86, optimize=True, progressive=True)
    print(OUT / name)


def concrete_floor(rng, img):
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W * SS, H * SS], fill=(132, 136, 132))
    for _ in range(30000):
        x, y = rng.randrange(W * SS), rng.randrange(H * SS)
        r = rng.randint(1, 4)
        d.ellipse([x - r, y - r, x + r, y + r], fill=shade(rng, (136, 140, 136), 14))
    for y in range(0, H * SS, 240 * SS):
        d.line([0, y, W * SS, y], fill=(116, 120, 116), width=2 * SS)


def soft_shadow(img, box, radius, offset=(14, 18), blur=16, strength=140):
    shadow = Image.new("L", img.size, 0)
    x0, y0, x1, y1 = box
    ox, oy = offset[0] * SS, offset[1] * SS
    ImageDraw.Draw(shadow).rounded_rectangle([x0 + ox, y0 + oy, x1 + ox, y1 + oy], radius, fill=strength)
    img.paste((24, 28, 26), (0, 0), shadow.filter(ImageFilter.GaussianBlur(blur * SS)))


def pipe(d, points, width, color, highlight):
    d.line(points, fill=color, width=width, joint="curve")
    d.line([(x - width * 0.18, y - width * 0.18) for x, y in points], fill=highlight, width=max(1, width // 4), joint="curve")


def nft_channel(rng, img, box):
    x0, y0, x1, y1 = box
    soft_shadow(img, box, 12 * SS)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(box, 12 * SS, fill=(226, 230, 226))
    h = y1 - y0
    d.rounded_rectangle([x0, y0, x1, y0 + h * 0.22], 12 * SS, fill=(244, 246, 242))
    d.rectangle([x0 + 6 * SS, y1 - h * 0.18, x1 - 6 * SS, y1 - 4 * SS], fill=(196, 202, 198))
    d.rounded_rectangle([x0 - 8 * SS, y0 - 4 * SS, x0 + 14 * SS, y1 + 4 * SS], 5 * SS, fill=(204, 208, 204))
    d.rounded_rectangle([x1 - 14 * SS, y0 - 4 * SS, x1 + 8 * SS, y1 + 4 * SS], 5 * SS, fill=(204, 208, 204))


def hydro_lettuce(rng, d, cx, cy, r, red):
    d.ellipse([cx - 22 * SS, cy - 22 * SS, cx + 22 * SS, cy + 22 * SS], fill=(36, 38, 36))
    outer = (146, 62, 78) if red else (92, 166, 62)
    inner = (196, 112, 116) if red else (166, 218, 98)
    for i in range(16):
        a = i / 16 * math.tau + rng.uniform(-0.15, 0.15)
        x, y = cx + math.cos(a) * r * 0.55, cy + math.sin(a) * r * 0.5
        lr = r * rng.uniform(0.40, 0.50)
        d.ellipse([x - lr, y - lr * 0.9, x + lr, y + lr * 0.9], fill=jitter(rng, outer, 14))
    for i in range(10):
        a = i / 10 * math.tau + 0.3
        x, y = cx + math.cos(a) * r * 0.27, cy + math.sin(a) * r * 0.25
        lr = r * 0.32
        d.ellipse([x - lr, y - lr, x + lr, y + lr], fill=jitter(rng, inner, 12))
    d.ellipse([cx - r * 0.16, cy - r * 0.16, cx + r * 0.16, cy + r * 0.16], fill=(214, 238, 156) if not red else (226, 166, 156))


def render_nft(name, seed):
    rng = random.Random(seed)
    img = Image.new("RGB", (W * SS, H * SS))
    concrete_floor(rng, img)
    d = ImageDraw.Draw(img)
    # rack frame
    for x in (96 * SS, (W - 112) * SS):
        soft_shadow(img, [x, 30 * SS, x + 16 * SS, int(H * 0.80) * SS], 4 * SS, blur=10)
        d = ImageDraw.Draw(img)
        d.rectangle([x, 30 * SS, x + 16 * SS, int(H * 0.80) * SS], fill=(150, 156, 162))
        d.line([x + 4 * SS, 30 * SS, x + 4 * SS, int(H * 0.80) * SS], fill=(196, 202, 208), width=3 * SS)
    # reservoir with lid, inspection opening, pump and air stone line
    tank = [380 * SS, int(H * 0.80) * SS, 900 * SS, (H - 18) * SS]
    soft_shadow(img, tank, 16 * SS, blur=20, strength=170)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle(tank, 16 * SS, fill=(38, 44, 52))
    d.rounded_rectangle([tank[0] + 10 * SS, tank[1] + 8 * SS, tank[2] - 10 * SS, tank[3] - 10 * SS], 12 * SS, fill=(52, 60, 70))
    hatch = [tank[0] + 40 * SS, tank[1] + 26 * SS, tank[0] + 240 * SS, tank[3] - 28 * SS]
    d.rounded_rectangle(hatch, 10 * SS, fill=(32, 92, 120))
    for _ in range(140):
        x = rng.randrange(hatch[0] + 8 * SS, hatch[2] - 8 * SS)
        y = rng.randrange(hatch[1] + 6 * SS, hatch[3] - 6 * SS)
        r = rng.randint(2, 5) * SS // 2
        d.ellipse([x - r, y - r, x + r, y + r], fill=jitter(rng, (120, 196, 220), 20))
    d.rounded_rectangle([tank[2] - 170 * SS, tank[1] + 24 * SS, tank[2] - 60 * SS, tank[3] - 26 * SS], 8 * SS, fill=(28, 30, 34))
    d.ellipse([tank[2] - 132 * SS, tank[1] + 36 * SS, tank[2] - 98 * SS, tank[1] + 70 * SS], fill=(34, 197, 94))
    # dosing bottles next to the tank
    for i, col in enumerate([(222, 178, 64), (88, 150, 214), (214, 88, 88), (150, 110, 200)]):
        bx = 940 * SS + i * 56 * SS
        by = int(H * 0.83) * SS
        soft_shadow(img, [bx, by, bx + 40 * SS, by + 70 * SS], 8 * SS, offset=(6, 8), blur=6)
        d = ImageDraw.Draw(img)
        d.rounded_rectangle([bx, by, bx + 40 * SS, by + 70 * SS], 8 * SS, fill=(236, 238, 234))
        d.rounded_rectangle([bx + 6 * SS, by + 26 * SS, bx + 34 * SS, by + 64 * SS], 5 * SS, fill=col)
        d.ellipse([bx + 12 * SS, by + 6 * SS, bx + 28 * SS, by + 20 * SS], fill=(60, 62, 64))
    # channels with lettuce heads
    rows = 5
    top, pitch, ch = 46 * SS, 112 * SS, 58 * SS
    x0, x1 = 150 * SS, (W - 150) * SS
    feed_x, drain_x = 128 * SS, (W - 128) * SS
    for i in range(rows):
        y = top + i * pitch
        nft_channel(rng, img, [x0, y, x1, y + ch])
    d = ImageDraw.Draw(img)
    # feed manifold (black) on the left, return (grey) on the right
    pipe(d, [(feed_x, top + ch // 2), (feed_x, tank[1] + 40 * SS), (tank[0] + 20 * SS, tank[1] + 40 * SS)], 12 * SS, (26, 28, 30), (90, 94, 98))
    pipe(d, [(drain_x, top + ch // 2), (drain_x, tank[1] - 20 * SS), (tank[2] - 30 * SS, tank[1] - 20 * SS), (tank[2] - 30 * SS, tank[1] + 10 * SS)],
         18 * SS, (178, 182, 180), (222, 226, 224))
    for i in range(rows):
        y = top + i * pitch + ch // 2
        pipe(d, [(feed_x, y), (x0 + 30 * SS, y - 8 * SS)], 6 * SS, (26, 28, 30), (90, 94, 98))
        d.ellipse([feed_x - 9 * SS, y - 9 * SS, feed_x + 9 * SS, y + 9 * SS], fill=(40, 42, 44))
        d.ellipse([drain_x - 13 * SS, y - 13 * SS, drain_x + 13 * SS, y + 13 * SS], fill=(160, 164, 162))
    for i in range(rows):
        y = top + i * pitch + ch // 2
        growth = 0.75 + 0.25 * (i / (rows - 1))
        for j, x in enumerate(range(x0 + 70 * SS, x1 - 40 * SS, 118 * SS)):
            r = 50 * SS * growth * rng.uniform(0.9, 1.08)
            hydro_lettuce(rng, d, x + rng.randint(-4, 4) * SS, y + rng.randint(-3, 3) * SS, r, red=(i + j) % 4 == 1)
    img = vignette(img.filter(ImageFilter.GaussianBlur(0.6 * SS)))
    img = img.resize((W, H), Image.LANCZOS)
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / name, "JPEG", quality=86, optimize=True, progressive=True)
    print(OUT / name)


if __name__ == "__main__":
    render("tomato-bed.jpg", "tomato", 7)
    render("lettuce-bed.jpg", "lettuce", 11)
    render_nft("nft-rack.jpg", 23)
