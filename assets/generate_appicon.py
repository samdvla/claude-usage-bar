#!/usr/bin/env python3
"""Generate the Claude Usage .app icon (.icns): a dark rounded-square app tile
with a white terminal window and '>' prompt. Original artwork drawn with Pillow.

Usage: python generate_appicon.py [output.icns]
Requires macOS `iconutil` to assemble the .icns from a generated .iconset.
"""
import os
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw


def draw(size):
    S = 4
    W = size * S
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # dark rounded-square tile
    bg = (18, 20, 24, 255)
    d.rounded_rectangle([0, 0, W, W], radius=int(W * 0.225), fill=bg)
    white = (255, 255, 255, 255)
    # terminal window outline
    m = int(W * 0.26)
    r = int(W * 0.11)
    sw = max(2, int(W * 0.040))
    d.rounded_rectangle([m, m, W - m, W - m], radius=r, outline=white, width=sw)
    # centered ">" chevron
    cw = max(2, int(W * 0.046))
    cx, cy = W * 0.50, W * 0.505
    arm = W * 0.085
    apex_x = cx + arm * 0.6
    d.line([(cx - arm, cy - arm), (apex_x, cy), (cx - arm, cy + arm)],
           fill=white, width=cw, joint="curve")
    return img.resize((size, size), Image.LANCZOS)


def generate(out_icns):
    with tempfile.TemporaryDirectory() as tmp:
        iconset = os.path.join(tmp, "AppIcon.iconset")
        os.makedirs(iconset)
        for base in (16, 32, 128, 256, 512):
            draw(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
            draw(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
        subprocess.run(["iconutil", "-c", "icns", iconset, "-o", out_icns],
                       check=True)
    return out_icns


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "AppIcon.icns")
    print("saved", generate(out))
