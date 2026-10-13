"""Small pure drawing helpers for the lyrics strip (colour maths, outlined text, text widths).
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
    hook = getattr(canvas, "draw_outline", None)    # the true-transparency renderer draws borders itself
    if hook is not None:
        hook(x, y, text, font, size, anchor, justify)
        return
    outline_hex = _rgb_to_hex(OUTLINE_RGB)
    for dx, dy in _outline_offsets(round(size, 2)):
        canvas.create_text(
            x + dx, y + dy, text=text, font=font, fill=outline_hex,
            anchor=anchor, justify=justify,
        )

def _draw_outlined_text(canvas, x, y, text, font, fill, size, anchor="nw", justify="left"):
    _draw_outline_only(canvas, x, y, text, font, size, anchor, justify)
    canvas.create_text(x, y, text=text, font=font, fill=fill, anchor=anchor, justify=justify)


class TextMeter:
    """Remembers what `font.measure()` answered. Every call is a round trip into Tk, and the
    karaoke line needs the width of every prefix of its text on every redraw (50 a second),
    but the answers only change when the text or the font size does. The font itself
    (family, weight) never changes after it is made, so (font name, size, text) is a complete key.

    The caches are bounded: when one fills up it is emptied and refills with what is on screen."""

    LIMIT = 4096

    def __init__(self):
        self._widths, self._prefixes, self._fits, self._clipped = {}, {}, {}, {}

    @staticmethod
    def _key(font):
        return str(font), font.cget("size")

    @classmethod
    def _store(cls, cache, key, value):
        if len(cache) >= cls.LIMIT:
            cache.clear()
        cache[key] = value
        return value

    @staticmethod
    def _set_size(font, size):
        if font.cget("size") != size:       # a redundant configure still makes Tk re-lay-out the font
            font.configure(size=size)

    def width(self, font, text):
        key = (self._key(font), text)
        w = self._widths.get(key)
        return w if w is not None else self._store(self._widths, key, font.measure(text))

    def prefix(self, font, text):
        """Widths of text[:0], text[:1] ... text[:len]. Positions taken from prefixes match how
        the whole string is laid out, which keeps per-character colouring lined up."""
        key = (self._key(font), text)
        p = self._prefixes.get(key)
        if p is None:
            p = self._store(self._prefixes, key, tuple(font.measure(text[:i]) for i in range(len(text) + 1)))
        return p

    def fit(self, font, text, base_size, min_size, max_width):
        """Shrink `font` (in place) from base_size to whatever fits `text` in max_width, stopping at
        min_size. Returns the size the font is left at."""
        key = (str(font), text, base_size, min_size, max_width)
        size = self._fits.get(key)
        if size is not None:
            self._set_size(font, size)
            return size
        size = base_size
        font.configure(size=size)
        if text:
            while size > min_size and font.measure(text) > max_width:
                size -= 1
                font.configure(size=size)
        return self._store(self._fits, key, size)

    def ellipsize(self, font, text, max_width):
        """`text` cut short with "..." so that it fits max_width."""
        key = (self._key(font), text, max_width)
        out = self._clipped.get(key)
        if out is None:
            out = text
            if font.measure(text) > max_width:
                while len(text) > 1 and font.measure(text + "...") > max_width:
                    text = text[:-1]
                out = text.rstrip() + "..."
            self._store(self._clipped, key, out)
        return out
