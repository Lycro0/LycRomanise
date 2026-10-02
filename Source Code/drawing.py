"""Small pure drawing helpers for the lyrics strip (colour maths, outlined text).
No app state and no runtime-changing settings live here, so it is safe to import anywhere."""
import math
from functools import lru_cache

def _rgb_to_hex(rgb):
    return "#%02x%02x%02x" % tuple(max(0, min(255, int(c))) for c in rgb)

def _lerp_rgb(c1, c2, t):
    t = max(0.0, min(1.0, t))
    return tuple(c1[i] + (c2[i] - c1[i]) * t for i in range(3))

def _ease_out_cubic(t):
    t = max(0.0, min(1.0, t))
    return 1 - (1 - t) ** 3

# tkinter canvas text has no native stroke, so copies in a near-black colour
# drawn just behind the real text fake one. It's (1, 1, 1), not pure black:
# DESKTOP_BG ("#000000") is the chroma-key colour the Windows transparent
# strip keys off, so a literal (0, 0, 0) outline would vanish like the
# background does.
OUTLINE_RGB = (1, 1, 1)

@lru_cache(maxsize=16)
def _outline_offsets(size):
    """Ring of offsets approximating a circle of radius `size` px."""
    if size <= 0:
        return ()
    n = 8 if size <= 1.0 else 12
    return tuple(
        (size * math.cos(2 * math.pi * k / n), size * math.sin(2 * math.pi * k / n))
        for k in range(n)
    )

def _draw_outline_only(canvas, x, y, text, font, size, anchor="nw", justify="left"):
    outline_hex = _rgb_to_hex(OUTLINE_RGB)
    for dx, dy in _outline_offsets(round(size, 2)):
        canvas.create_text(
            x + dx, y + dy, text=text, font=font, fill=outline_hex,
            anchor=anchor, justify=justify,
        )

def _draw_outlined_text(canvas, x, y, text, font, fill, size, anchor="nw", justify="left"):
    _draw_outline_only(canvas, x, y, text, font, size, anchor, justify)
    canvas.create_text(x, y, text=text, font=font, fill=fill, anchor=anchor, justify=justify)
