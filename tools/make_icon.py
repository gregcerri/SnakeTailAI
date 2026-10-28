"""Builds the SnakeTail AI icon (app.png + multi-size app.ico) into src/SnakeTailAI/Assets.

It starts from the original SnakeTail icon (tools/icon-source/original-<size>.png, extracted from SnakeTail.exe)
and retouches every size: livelier colours, a little more contrast, crisper edges and a lighter, softer shadow.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "icon-source")
OUT = os.path.join(HERE, "..", "src", "SnakeTailAI", "Assets")
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def retouch(img, size):
    rgba = np.asarray(img.convert("RGBA"), np.float32) / 255
    rgb, alpha = rgba[..., :3], rgba[..., 3]

    mx, mn = rgb.max(axis=2), rgb.min(axis=2)
    saturation = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0)

    # The drop shadow is the grey, half-transparent part: make it lighter.
    shadow = (saturation < 0.15) & (alpha < 0.85) & (mx < 0.75)
    alpha = np.where(shadow, alpha * 0.55, alpha)

    # Livelier colours: push saturated pixels (body, spots) further from grey, leave whites and greys alone.
    grey = rgb.mean(axis=2, keepdims=True)
    boost = 1.0 + 0.22 * np.clip(saturation * 2, 0, 1)[..., None]
    rgb = grey + (rgb - grey) * boost

    # A little more contrast around mid-tones.
    rgb = np.clip((rgb - 0.5) * 1.08 + 0.5, 0, 1)

    out = Image.fromarray((np.dstack([rgb, alpha[..., None]]) * 255).round().astype(np.uint8))

    # Crisper edges; small sizes only get a light touch so their hand-drawn pixels survive.
    radius, percent = (1.6, 55) if size >= 128 else (0.8, 45) if size >= 40 else (0.5, 30)
    sharp = out.convert("RGB").filter(ImageFilter.UnsharpMask(radius=radius, percent=percent, threshold=1))
    sharp.putalpha(out.getchannel("A"))

    # Softer shadow edge on the big sizes.
    if size >= 128:
        a = np.asarray(sharp.getchannel("A"), np.float32)
        soft = np.asarray(sharp.getchannel("A").filter(ImageFilter.GaussianBlur(2)), np.float32)
        sharp.putalpha(Image.fromarray(np.where(shadow, soft, a).clip(0, 255).astype(np.uint8)))
    return sharp


def main():
    os.makedirs(OUT, exist_ok=True)
    icons = {}
    for size in SIZES:
        path = os.path.join(SOURCE, f"original-{size}.png")
        if not os.path.exists(path):
            sys.exit(f"missing {path}")
        icons[size] = retouch(Image.open(path), size)

    icons[256].save(os.path.join(OUT, "app.png"))
    icons[256].save(os.path.join(OUT, "app.ico"), sizes=[(s, s) for s in SIZES[::-1]],
                    append_images=[icons[s] for s in SIZES if s != 256])
    print("icon written to", os.path.normpath(OUT))


if __name__ == "__main__":
    sys.exit(main())
