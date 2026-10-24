#!/usr/bin/env python3
"""Renders the stylized top-down garden-bed JPEGs bundled with the sensor emulator.

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


if __name__ == "__main__":
    render("tomato-bed.jpg", "tomato", 7)
    render("lettuce-bed.jpg", "lettuce", 11)
