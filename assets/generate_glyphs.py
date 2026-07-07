#!/usr/bin/env python3
"""Generate menu-bar template glyphs for the providers ccc tracks.

Fetches the monochrome logomarks from simple-icons (CC0-licensed icon set;
the marks themselves remain their owners' trademarks — used nominatively to
label each provider's own usage number) and rasterizes them to 64x64
black-on-transparent PNGs, which macOS template-image mode recolors for
light/dark menu bars automatically.

Pinned to a specific simple-icons commit rather than `develop`: OpenAI's
logomark was pulled from simple-icons' develop branch on 2025-11-29 (PR #13944,
pending their 2025 brand refresh) and has not been re-added, so `develop` 404s
for "openai" today. The pinned commit below is the last one where both
icons/claude.svg and icons/openai.svg existed side by side, giving us OpenAI's
classic blossom/knot mark from a single reproducible ref.

Slug note: simple-icons ships two separate Anthropic-family entries — "anthropic"
(a wordmark/lettermark, not what we want here) and "claude" (the spark/asterisk
mark used for the Claude product, which is what this glyph is for). We use
"claude", not "anthropic".

Usage: python3 generate_glyphs.py        (needs: pip install cairosvg)
"""
import os
import urllib.request

import cairosvg

HERE = os.path.dirname(os.path.abspath(__file__))
ICONS = {"claude-glyph.png": "claude", "codex-glyph.png": "openai"}
REF = "cf471df7755a247c90fc615cdeb4b14daf678e6b"
URL = "https://raw.githubusercontent.com/simple-icons/simple-icons/" + REF + "/icons/{}.svg"

for out, slug in ICONS.items():
    svg = urllib.request.urlopen(URL.format(slug), timeout=30).read()
    path = os.path.join(HERE, out)
    cairosvg.svg2png(bytestring=svg, write_to=path,
                     output_width=64, output_height=64)
    print("saved", path)
