"""Line splitting and karaoke timing, kept free of any GUI code.

A lyric line that is too long to read comfortably is cut into chunks at word
boundaries. Each chunk becomes its own "line": the first is shown big, and the
rest arrive later as the *next* line, so you never see two lines stacked
under the top one.

Timing works on the whole original line and is then shared out:
  * every line has a fill fraction (0..1) for "how much of it has been sung",
    taken from real per-word timing when the source has it, otherwise
    estimated from the line's length;
  * a chunk covers the slice [f0, f1] of that fraction, so its own fill is
    (line_fraction - f0) / (f1 - f0), and it becomes the current line at the
    moment the line's fraction reaches f0.
"""

import math
from bisect import bisect_right

class Seg:
    """One displayed line (a whole lyric line, or one chunk of a long one)."""
    __slots__ = ("line", "start", "text", "f0", "f1")

    def __init__(self, line, start, text, f0, f1):
        self.line, self.start, self.text, self.f0, self.f1 = line, start, text, f0, f1

    def __repr__(self):
        return f"Seg(line={self.line}, start={self.start:.2f}, {self.text!r}, {self.f0:.2f}-{self.f1:.2f})"

class LineInfo:
    """Timing data for one original lyric line."""
    __slots__ = ("ts", "text", "span")

    def __init__(self, ts, text, span):
        self.ts, self.text, self.span = ts, text, span

def split_display(text, max_chars):
    """Cut `text` into balanced chunks of at most ~max_chars, at spaces.
    Returns [(chunk, f0, f1)] where f0/f1 are the chunk's start/end as a
    fraction of the whole text."""
    if max_chars <= 0 or len(text) <= max_chars or not text:
        return [(text, 0.0, 1.0)]

    words = text.split(" ")
    total = len(text)
    k = math.ceil(total / max_chars)

    # end position (just past the word and its following space) of each word
    ends = []
    c = 0
    for w in words:
        c += len(w) + 1
        ends.append(c)

    cuts = set()
    if len(words) > 1:
        for j in range(1, k):
            target = total * j / k
            best = min(range(len(words) - 1), key=lambda i: abs(ends[i] - target))
            cuts.add(best)

    pieces = []
    prev = 0
    for cut in sorted(cuts):
        pieces.append(" ".join(words[prev:cut + 1]))
        prev = cut + 1
    pieces.append(" ".join(words[prev:]))

    # A chunk with no spaces that is still far too long (unspaced script, one
    # giant token) gets hard-split by characters as a last resort.
    final = []
    for piece in pieces:
        if len(piece) > max_chars * 1.5 and " " not in piece:
            n = math.ceil(len(piece) / max_chars)
            step = math.ceil(len(piece) / n)
            final.extend(piece[i:i + step] for i in range(0, len(piece), step))
        else:
            final.append(piece)

    out = []
    pos = 0
    for idx, piece in enumerate(final):
        start = pos
        pos += len(piece) + (1 if idx < len(final) - 1 else 0)
        out.append((piece, start / total, 1.0 if idx == len(final) - 1 else pos / total))
    return out

def line_fraction(info, position):
    """How much of the original line has been sung at `position` (0..1)."""
    if info.span <= 0:
        return 1.0
    return max(0.0, min(1.0, (position - info.ts) / info.span))

def seg_fraction(seg, info, position):
    """Karaoke fill (0..1) for a single displayed segment."""
    lf = line_fraction(info, position)
    width = seg.f1 - seg.f0
    if width <= 1e-6:
        return 1.0
    return max(0.0, min(1.0, (lf - seg.f0) / width))

def build_segments(lines, displays, max_chars, last_span, chars_per_second, min_fill):
    """lines: [(ts, raw_text)]; displays: the text as it will be shown, one per
    line. Returns (segments, line_infos)."""
    segs, infos = [], []
    n = len(lines)
    for i, (ts, _raw) in enumerate(lines):
        text = displays[i]
        avail = (lines[i + 1][0] - ts) if i + 1 < n else last_span
        est = max(min_fill, len(text) / chars_per_second)
        info = LineInfo(ts, text, max(0.05, min(avail, est)))
        infos.append(info)

        next_ts = lines[i + 1][0] if i + 1 < n else None
        floor = segs[-1].start if segs else ts
        for k, (chunk, f0, f1) in enumerate(split_display(text, max_chars)):
            if k == 0:
                start = ts
            else:
                start = ts + f0 * info.span
                if next_ts is not None:
                    start = min(start, next_ts - 0.05)
            start = max(start, floor)
            floor = start
            segs.append(Seg(i, start, chunk, f0, f1))
    return segs, infos

def index_for_position(seg_starts, position):
    """Index of the segment current at `position`, or -1 before the first."""
    return bisect_right(seg_starts, position) - 1
