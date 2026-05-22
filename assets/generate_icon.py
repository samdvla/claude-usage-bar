#!/usr/bin/env python3
"""Generate the ccc menu bar template icon: a rounded terminal window with a
centered '>' chevron. Pure original artwork drawn with Pillow — no external
assets. Output is a 44x44 black-on-transparent PNG suitable as a macOS template
image (it adapts to light/dark menu bars automatically).

Usage: python generate_icon.py [output.png]
"""
import os
import sys

from PIL import Image, ImageDraw


def generate(out_path):
    S = 4                       # supersample, downscaled at the end for smooth edges
    W = 44 * S
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    black = (0, 0, 0, 255)
    m = 5 * S                   # margin
    r = 10 * S                  # corner radius
    sw = 3 * S                  # window stroke
    d.rounded_rectangle([m, m, W - m, W - m], radius=r, outline=black, width=sw)
    # centered ">" chevron
    cw = int(3.4 * S)
    cx, cy = W * 0.50, W * 0.50
    arm = W * 0.13
    apex_x = cx + arm * 0.55
    d.line([(cx - arm, cy - arm), (apex_x, cy), (cx - arm, cy + arm)],
           fill=black, width=cw, joint="curve")
    img = img.resize((44, 44), Image.LANCZOS)
    img.save(out_path)
    return out_path


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "icon.png")
    print("saved", generate(out))
