"""Window diagnostics for the lyrics strip: a screen grab that counts lyric-coloured pixels, and
the one-line 'window diagnostics (...)' log entry. Functions take the app object and the current
colours as arguments, so runtime changes to the colours are always seen."""
import logging
import sys

import winsys

log = logging.getLogger("spoti.app")


def count_lyric_pixels(app, targets):
    """Screen-grab the strip region and count pixels near the lyric colours (targets: RGB tuples).
    Returns the count, or None when it can't be measured."""
    try:
        from PIL import ImageGrab
        x, y = app.root.winfo_rootx(), app.root.winfo_rooty()
        img = ImageGrab.grab(bbox=(x, y, x + app.strip_w, y + app.strip_h), all_screens=True).convert("RGB")
    except Exception as exc:
        log.warning("lyric pixel check unavailable: %s", exc)
        return None
    targets = [tuple(t) for t in targets]
    try:
        import numpy as np
        arr = np.asarray(img, dtype=np.int16)
        hit = np.zeros(arr.shape[:2], dtype=bool)
        for t in targets:
            hit |= (np.abs(arr - np.array(t, dtype=np.int16)) < 40).all(axis=2)
        return int(hit.sum())
    except ImportError:
        pass
    # No numpy: look at every 3rd pixel in each direction (plenty to tell 0 from many).
    w, h = img.size
    small = img.resize((max(1, w // 3), max(1, h // 3)), 0)      # 0 = nearest neighbour
    n = 0
    for px in small.getdata():
        for t in targets:
            if abs(px[0] - t[0]) < 40 and abs(px[1] - t[1]) < 40 and abs(px[2] - t[2]) < 40:
                n += 1
                break
    return n


def log_window_diagnostics(app, reason, targets):
    """File-only facts about the strip window, to tell 'not drawn' from 'drawn but
    not shown'. Never raises."""
    try:
        r = app.root
        parts = ["window diagnostics (%s):" % reason]
        parts.append("mapped=%s state=%s geometry=%s" % (r.winfo_ismapped(), r.state(), r.geometry()))
        for opt in ("-topmost", "-alpha", "-transparentcolor"):
            try:
                parts.append("%s=%r" % (opt, r.attributes(opt)))
            except Exception as exc:
                parts.append("%s=err(%s)" % (opt, exc))
        c = app.desktop_canvas
        if c is not None:
            parts.append("canvas=%sx%s items=%s" % (c.winfo_width(), c.winfo_height(), len(c.find_all())))
        if sys.platform.startswith("win"):
            try:
                parts.append("win32=%s" % (winsys.describe_window(r.winfo_id()),))
            except Exception as exc:
                parts.append("win32=err(%s)" % exc)
            px = count_lyric_pixels(app, targets)
            if px is not None:
                parts.append("| lyric pixels on screen: %d" % px)
        log.info(" ".join(parts))
    except Exception:
        log.warning("window diagnostics failed", exc_info=True)
