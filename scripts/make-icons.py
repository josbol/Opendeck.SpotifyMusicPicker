#!/usr/bin/env python3
"""Draws the action icons (144 px + @2x) with Pillow. Run once; the PNGs are committed."""
import math, os, sys
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugin", "com.josbol.spotifymusicpicker.sdPlugin", "icons")
GREEN = (29, 185, 84); BG = (18, 18, 18); CARD = (34, 34, 40); WHITE = (243, 244, 246); MUTED = (163, 168, 181)

def canvas():
    img = Image.new("RGBA", (288, 288), BG + (255,))
    return img, ImageDraw.Draw(img)

def note(d, cx, cy, s, color):
    # a simple eighth note
    d.ellipse((cx - s * 0.55, cy + s * 0.25, cx + s * 0.25, cy + s * 0.85), fill=color)
    d.rectangle((cx + s * 0.1, cy - s * 0.9, cx + s * 0.25, cy + s * 0.55), fill=color)
    d.polygon([(cx + s * 0.1, cy - s * 0.9), (cx + s * 0.8, cy - s * 0.55), (cx + s * 0.8, cy - s * 0.15), (cx + s * 0.25, cy - s * 0.45)], fill=color)

def plugin():
    img, d = canvas()
    d.ellipse((34, 34, 254, 254), fill=GREEN)
    for i, w in enumerate((150, 120, 90)):
        y = 108 + i * 34
        d.arc((144 - w // 2, y - 20, 144 + w // 2, y + 60), 200, 340, fill=BG, width=16)
    return img

def slot():
    img, d = canvas()
    d.rounded_rectangle((28, 28, 260, 260), 26, fill=CARD)
    d.rectangle((28, 150, 260, 260), fill=(0, 0, 0, 120))
    note(d, 150, 100, 60, GREEN)
    d.rounded_rectangle((52, 190, 200, 210), 8, fill=WHITE)
    d.rounded_rectangle((52, 222, 150, 238), 8, fill=MUTED)
    return img

def nowplaying():
    img, d = canvas()
    d.rounded_rectangle((28, 28, 260, 260), 26, fill=CARD)
    for i, h in enumerate((70, 120, 90, 140, 60)):
        x = 74 + i * 32
        d.rounded_rectangle((x, 180 - h, x + 20, 180), 8, fill=GREEN)
    d.rounded_rectangle((52, 214, 236, 226), 6, fill=(60, 60, 70))
    d.rounded_rectangle((52, 214, 150, 226), 6, fill=GREEN)
    return img

def transport(next_):
    img, d = canvas()
    cx, cy = 144, 144
    s = 1 if next_ else -1
    d.polygon([(cx - 60 * s, cy - 52), (cx + 4 * s, cy), (cx - 60 * s, cy + 52)], fill=WHITE)
    d.polygon([(cx + 4 * s, cy - 52), (cx + 68 * s, cy), (cx + 4 * s, cy + 52)], fill=WHITE)
    d.rectangle((min(cx + 68 * s, cx + 82 * s), cy - 52, max(cx + 68 * s, cx + 82 * s), cy + 52), fill=WHITE)
    return img

def dial():
    img, d = canvas()
    d.ellipse((40, 40, 248, 248), outline=MUTED, width=14)
    d.ellipse((92, 92, 196, 196), fill=GREEN)
    for a in range(0, 360, 30):
        r1, r2 = 108, 122
        x1 = 144 + r1 * math.cos(math.radians(a)); y1 = 144 + r1 * math.sin(math.radians(a))
        x2 = 144 + r2 * math.cos(math.radians(a)); y2 = 144 + r2 * math.sin(math.radians(a))
        d.line((x1, y1, x2, y2), fill=WHITE, width=6)
    d.polygon([(130, 120), (166, 144), (130, 168)], fill=BG)
    return img

def browse():
    img, d = canvas()
    d.ellipse((40, 40, 248, 248), outline=MUTED, width=14)
    for i in range(3):
        d.rounded_rectangle((84 + i * 46, 118, 118 + i * 46, 170), 8, fill=GREEN if i == 1 else CARD)
    d.polygon([(66, 144), (86, 128), (86, 160)], fill=WHITE)
    d.polygon([(222, 144), (202, 128), (202, 160)], fill=WHITE)
    return img

def main():
    os.makedirs(OUT, exist_ok=True)
    icons = {"plugin": plugin(), "slot": slot(), "nowplaying": nowplaying(), "previous": transport(False), "next": transport(True), "dial": dial(), "browse": browse()}
    for name, img in icons.items():
        img.save(os.path.join(OUT, f"{name}@2x.png"), optimize=True)
        img.resize((144, 144), Image.Resampling.LANCZOS).save(os.path.join(OUT, f"{name}.png"), optimize=True)
    print(f"wrote {len(icons) * 2} icons to {OUT}")

if __name__ == "__main__":
    sys.exit(main())
