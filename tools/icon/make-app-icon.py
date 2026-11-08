# SPDX-License-Identifier: GPL-3.0-or-later
"""Generate the keepassity application icon.

The committed SVG (app-icon.svg) is the editable source of the design; this
script renders it to a multi-size Windows .ico plus PNG previews. Run from the
repository root:

    python tools/icon/make-app-icon.py

Outputs:
    apps/windows/Keepassity.Mock/Assets/app.ico          (16..256 px frames)
    apps/windows/Keepassity.Mock/Assets/app-icon-256.png
    apps/windows/Keepassity.Mock/Assets/app-icon-512.png

No network, no external assets: the mark is drawn geometrically so it stays
crisp at 16 px (tray) and 256 px (store/About).
"""

from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw

CANVAS = 1024
SUPERSAMPLE = 4
ASSETS = pathlib.Path("apps/windows/Keepassity.Mock/Assets")

# Palette: deep ink to teal, white mark, dark keyhole.
INK = (11, 27, 43)
TEAL = (31, 166, 160)
WHITE = (246, 250, 252)
KEYHOLE = (11, 27, 43)


def diagonal_gradient(size: int) -> Image.Image:
    """45-degree gradient from INK to TEAL."""
    image = Image.new("RGB", (size, size))
    pixels = image.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            pixels[x, y] = tuple(
                round(INK[i] + (TEAL[i] - INK[i]) * t) for i in range(3)
            )
    return image


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=radius, fill=255
    )
    return mask


def draw_shield(draw: ImageDraw.ImageDraw, s: int) -> None:
    """White shield with a dark keyhole, centred, in a 1024-space scaled by s."""
    # Shield body: rounded top block unioned with a tapered point.
    left, right, top = 268 * s, 756 * s, 196 * s
    shoulder = 566 * s
    tip = 856 * s
    draw.rounded_rectangle(
        (left, top, right, shoulder), radius=86 * s, fill=WHITE
    )
    draw.polygon(
        [
            (left, shoulder - 80 * s),
            (right, shoulder - 80 * s),
            (right, shoulder),
            (512 * s, tip),
            (left, shoulder),
        ],
        fill=WHITE,
    )
    # Keyhole: circle + tapered stem, cut out of the shield.
    radius = 96 * s
    centre = (512 * s, 446 * s)
    draw.ellipse(
        (centre[0] - radius, centre[1] - radius, centre[0] + radius, centre[1] + radius),
        fill=KEYHOLE,
    )
    draw.polygon(
        [
            (centre[0] - 52 * s, centre[1] + 40 * s),
            (centre[0] + 52 * s, centre[1] + 40 * s),
            (centre[0] + 96 * s, 690 * s),
            (centre[0] - 96 * s, 690 * s),
        ],
        fill=KEYHOLE,
    )


def render(size: int) -> Image.Image:
    """Render one full-size RGBA frame with supersampling."""
    big = size * SUPERSAMPLE
    background = diagonal_gradient(big).convert("RGBA")
    background.putalpha(rounded_mask(big, int(big * 0.196)))
    frame = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    frame.alpha_composite(background)
    shield = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw_shield(ImageDraw.Draw(shield), big / CANVAS)
    frame.alpha_composite(shield)
    return frame.resize((size, size), Image.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    frames = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    rendered = {size: render(size) for size in frames}
    ico_path = ASSETS / "app.ico"
    rendered[256].save(
        ico_path,
        format="ICO",
        sizes=[(size, size) for size in frames],
        append_images=[rendered[size] for size in frames if size != 256],
    )
    rendered[256].save(ASSETS / "app-icon-256.png", format="PNG")
    render(512).save(ASSETS / "app-icon-512.png", format="PNG")
    print(f"wrote {ico_path} ({ico_path.stat().st_size} bytes)")
    print(f"wrote {ASSETS / 'app-icon-256.png'}")
    print(f"wrote {ASSETS / 'app-icon-512.png'}")


if __name__ == "__main__":
    main()
