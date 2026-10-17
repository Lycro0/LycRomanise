"""True per-pixel transparency for the lyrics strip (always used when Pillow and Segoe UI are there).

The old strip was see-through because Windows hid one colour (a colour key). That is fine on
screen, but OBS records the hidden colour as plain black. So the strip is drawn as an
image with a real alpha channel and handed to Windows with UpdateLayeredWindow, so the same
window is see-through everywhere, including OBS Window Capture.

The app keeps using its normal drawing code: StripRenderer stands in for the Tk canvas (create_text,
create_rectangle, delete) and paints with Pillow instead. If anything here fails, the app switches
back to the colour-key strip.
"""

import logging
import math
import os

log = logging.getLogger("lycro.alpha")

try:
    from PIL import Image, ImageDraw, ImageFont
except Exception:                      # Pillow missing: the feature is simply unavailable
    Image = ImageDraw = ImageFont = None

WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
WS_POPUP = 0x80000000
GWLP_HWNDPARENT = -8
SW_HIDE, SW_SHOWNA = 0, 8
TILE_LIMIT = 48


def _font_file(bold):
    folder = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
    path = os.path.join(folder, "segoeuib.ttf" if bold else "segoeui.ttf")
    return path if os.path.isfile(path) else None


# Segoe UI has no glyph for the music note that marks an instrumental break (U+266A); drawing it
# with Segoe UI alone gives the empty "missing glyph" box. Characters the main font lacks are drawn
# with one of these instead (Tk's own canvas does this by itself; the Pillow strip has to be told).
FALLBACK_FONT_NAMES = ("seguisym.ttf", "segoeuib.ttf", "arial.ttf", "msgothic.ttc", "malgun.ttf", "simsun.ttc")


def _fallback_font_files():
    folder = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
    return [p for p in (os.path.join(folder, n) for n in FALLBACK_FONT_NAMES) if os.path.isfile(p)]


def available(font_file=_font_file):
    return Image is not None and font_file(False) is not None and font_file(True) is not None


def _rgba(value, default=(255, 255, 255, 255)):
    """'#rrggbb' or an (r, g, b) tuple -> (r, g, b, 255)."""
    try:
        if isinstance(value, str):
            v = value.lstrip("#")
            return (int(v[0:2], 16), int(v[2:4], 16), int(v[4:6], 16), 255)
        return tuple(int(c) for c in value[:3]) + (255,)
    except (TypeError, ValueError, IndexError):
        return default


class StripRenderer:
    """Draws what the strip's code asks of a Tk canvas onto a transparent RGBA image."""

    def __init__(self, meter, dpi=96.0, font_file=_font_file, fallback_files=_fallback_font_files):
        self.meter, self.dpi, self._font_file = meter, float(dpi), font_file
        self._fallback_files = fallback_files
        self.img = self._draw = None
        self._fonts, self._tiles = {}, {}
        self._glyph_fonts = {}          # (bold, px, character) -> the Pillow font that can draw it
        self._notdef = {}               # (bold, px) -> what a missing glyph looks like in the main font

    # --- frame ------------------------------------------------------------

    def begin(self, w, h, hit_testable=True):
        """Start a frame. Alpha 1 (invisible) rather than 0 keeps the empty strip draggable."""
        self.img = Image.new("RGBA", (int(w), int(h)), (0, 0, 0, 1 if hit_testable else 0))
        self._draw = ImageDraw.Draw(self.img)

    def delete(self, *_a, **_k):           # the frame starts empty, so nothing to clear
        pass

    # --- fonts and text layout ---------------------------------------------

    def _pil_font(self, font):
        bold = font.cget("weight") == "bold"
        size = int(font.cget("size"))
        px = max(1, round(size * self.dpi / 72.0)) if size > 0 else max(1, -size)
        key = (bold, px)
        f = self._fonts.get(key)
        if f is None:
            f = self._fonts[key] = ImageFont.truetype(self._font_file(bold), px)
        return key, f

    @staticmethod
    def _glyph_bytes(pil_font, ch):
        try:
            mask = pil_font.getmask(ch)
            return (mask.size, bytes(mask))
        except Exception:
            return None

    def _has_glyph(self, pil_font, key, ch):
        """False when `pil_font` would draw `ch` as the empty 'missing glyph' box."""
        if ord(ch) < 0x250:                # Latin text: always there
            return True
        ref = self._notdef.get(key)
        if ref is None:
            ref = self._notdef[key] = self._glyph_bytes(pil_font, "\uffff") or False
        got = self._glyph_bytes(pil_font, ch)
        if got is None:
            return False
        return not (ref and got == ref)

    def _font_for(self, key, pil_font, ch):
        """The font to draw `ch` with: the main one, or the first fallback font that has the glyph."""
        ck = (key, ch)
        f = self._glyph_fonts.get(ck)
        if f is not None:
            return f
        f = pil_font
        if not self._has_glyph(pil_font, key, ch):
            bold, px = key
            for path in self._fallback_files():
                try:
                    cand = ImageFont.truetype(path, px)
                except Exception:
                    continue
                if self._has_glyph(cand, ("fb", path, px), ch):
                    f = cand
                    break
        if len(self._glyph_fonts) > 4096:
            self._glyph_fonts.clear()
        self._glyph_fonts[ck] = f
        return f

    def _origin(self, x, y, text, font, anchor, pil_font):
        p = self.meter.prefix(font, text)
        asc, desc = pil_font.getmetrics()
        ls = asc + desc
        a = (anchor or "nw").lower()
        if a == "center":
            h, v = "c", "m"
        else:
            h = "w" if "w" in a else ("e" if "e" in a else "c")
            v = "n" if "n" in a else ("s" if "s" in a else "m")
        left = x if h == "w" else (x - p[-1] if h == "e" else x - p[-1] / 2.0)
        top = y if v == "n" else (y - ls if v == "s" else y - ls / 2.0)
        return p, left, top, ls

    # --- the canvas calls the strip uses ------------------------------------

    def create_text(self, x, y, text="", font=None, fill="#ffffff", anchor="nw", justify=None, **_k):
        if not text:
            return
        key, pf = self._pil_font(font)
        color = _rgba(fill)
        if len(text) == 1 and (anchor or "nw") == "nw":
            self._draw.text((x, y), text, font=self._font_for(key, pf, text), fill=color, anchor="la")
            return
        p, left, top, _ls = self._origin(x, y, text, font, anchor, pf)
        for i, ch in enumerate(text):
            if ch != " ":
                self._draw.text((left + p[i], top), ch, font=self._font_for(key, pf, ch), fill=color, anchor="la")

    def create_rectangle(self, x0, y0, x1, y1, outline="", width=1, **_k):
        if outline:
            self._draw.rectangle([x0, y0, x1, y1], outline=_rgba(outline), width=max(1, int(width)))

    def draw_outline(self, x, y, text, font, size, anchor="nw", justify=None):
        """The dark text border. Built once per (text, font, size) as a small image and then
        just laid on the frame, so it costs one paste per frame instead of dozens of draws."""
        if not text or size <= 0:
            return
        import drawing
        from drawing import _outline_offsets
        key, pf = self._pil_font(font)
        p, left, top, ls = self._origin(x, y, text, font, anchor, pf)
        pad = int(math.ceil(size)) + 2
        orgb = tuple(drawing.OUTLINE_RGB)
        tkey = (key, text, round(size, 2), orgb)
        tile = self._tiles.get(tkey)
        if tile is None:
            if len(self._tiles) >= TILE_LIMIT:
                self._tiles.clear()
            tile = Image.new("RGBA", (int(math.ceil(p[-1])) + 2 * pad, ls + 2 * pad), (0, 0, 0, 0))
            d = ImageDraw.Draw(tile)
            for dx, dy in _outline_offsets(round(size, 2)):
                for i, ch in enumerate(text):
                    if ch != " ":
                        d.text((pad + p[i] + dx, pad + dy), ch, font=self._font_for(key, pf, ch),
                               fill=orgb + (255,), anchor="la")
            self._tiles[tkey] = tile
        self._over(tile, int(round(left)) - pad, int(round(top)) - pad)

    def _over(self, tile, x, y):
        """Alpha-composite `tile` onto the frame at (x, y), clipped to the frame."""
        w, h = self.img.size
        l, t = max(0, -x), max(0, -y)
        r, b = min(tile.width, w - x), min(tile.height, h - y)
        if r > l and b > t:
            self.img.alpha_composite(tile, (x + l, y + t), (l, t, r, b))


class AlphaStrip(StripRenderer):
    """StripRenderer plus the Windows side: puts each finished frame on the strip's window."""

    def __init__(self, root, meter):
        import ctypes
        from ctypes import wintypes
        import winsys
        super().__init__(meter, dpi=root.winfo_fpixels("1i"))
        self.root, self.failed = root, False
        self.taskbar = False             # True: the strip gets its own taskbar button
        self._ct, self._wt, self._winsys = ctypes, wintypes, winsys
        self._api = winsys._hwnd_api()
        self._ulw = ctypes.WinDLL("user32", use_last_error=True)
        gdi = self._gdi = ctypes.WinDLL("gdi32", use_last_error=True)
        v, u32, i32 = ctypes.c_void_p, ctypes.c_uint32, ctypes.c_int
        self._ulw.GetDC.restype, self._ulw.GetDC.argtypes = v, (v,)
        self._ulw.ReleaseDC.argtypes = (v, v)
        self._ulw.ShowWindow.argtypes = (v, i32)
        self._ulw.SetWindowTextW.argtypes = (v, wintypes.LPCWSTR)
        self._ulw.SetWindowLongPtrW = getattr(self._ulw, "SetWindowLongPtrW", None) or self._ulw.SetWindowLongW
        self._ulw.SetWindowLongPtrW.restype = v
        self._ulw.SetWindowLongPtrW.argtypes = (v, i32, v)
        self._ulw.CreateWindowExW.restype = v
        self._ulw.CreateWindowExW.argtypes = (u32, wintypes.LPCWSTR, wintypes.LPCWSTR, u32,
                                              i32, i32, i32, i32, v, v, v, v)
        self._ulw.DestroyWindow.argtypes = (v,)
        self._ulw.UpdateLayeredWindow.restype = wintypes.BOOL
        self._ulw.UpdateLayeredWindow.argtypes = (v, v, v, v, v, v, u32, v, u32)
        gdi.CreateCompatibleDC.restype, gdi.CreateCompatibleDC.argtypes = v, (v,)
        gdi.CreateDIBSection.restype = v
        gdi.CreateDIBSection.argtypes = (v, v, u32, ctypes.POINTER(v), v, u32)
        gdi.SelectObject.restype, gdi.SelectObject.argtypes = v, (v, v)
        gdi.DeleteObject.argtypes = (v,)
        gdi.DeleteDC.argtypes = (v,)

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [("biSize", u32), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                        ("biPlanes", ctypes.c_ushort), ("biBitCount", ctypes.c_ushort),
                        ("biCompression", u32), ("biSizeImage", u32), ("biXPelsPerMeter", ctypes.c_long),
                        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", u32), ("biClrImportant", u32)]

        class BLENDFUNCTION(ctypes.Structure):
            _fields_ = [("BlendOp", ctypes.c_ubyte), ("BlendFlags", ctypes.c_ubyte),
                        ("SourceConstantAlpha", ctypes.c_ubyte), ("AlphaFormat", ctypes.c_ubyte)]

        self._BIH, self._BLEND = BITMAPINFOHEADER, BLENDFUNCTION
        self._hwnd = self._owner = None
        self._dib = self._bits = self._old = self._dc = None
        self._dib_size = None
        self._errors = 0
        self._reset_tried = False
        self._last_error = 0

    # --- window ---------------------------------------------------------------

    def _attach(self):
        """Give the strip's real Windows window the layered style. Needs the window to be mapped."""
        if not self.root.winfo_ismapped():
            return False
        hwnd = self._winsys.top_level_hwnd(self.root.winfo_id(), self._api)
        style = self._api.GetWindowLongW(hwnd, self._winsys.GWL_EXSTYLE)
        self._style_before = style
        # Tk marks a borderless window as a "tool window", and OBS leaves tool windows out of its
        # Window Capture list. Make it an ordinary window. By default an invisible owner window keeps
        # it off the taskbar (owned windows get no taskbar button); with "Show in the taskbar" on,
        # there is no owner and the window is flagged as an app window so it gets a button.
        style = (style | self._winsys.WS_EX_LAYERED) & ~WS_EX_TOOLWINDOW
        if self.taskbar:
            style |= WS_EX_APPWINDOW
        else:
            style &= ~WS_EX_APPWINDOW
        self._owner = None if self.taskbar else self._make_owner()
        u = self._ulw
        u.ShowWindow(hwnd, SW_HIDE)
        # UpdateLayeredWindow fails (error 87) on a window that SetLayeredWindowAttributes has ever been
        # called on, which Tk and the click-through code do. Clearing the layered bit and setting it
        # again wipes that history, whatever order startup happened to run in.
        self._api.SetWindowLongW(hwnd, self._winsys.GWL_EXSTYLE, style & ~self._winsys.WS_EX_LAYERED)
        self._api.SetWindowLongW(hwnd, self._winsys.GWL_EXSTYLE, style)
        if self._owner:
            u.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, self._owner)
        u.SetWindowTextW(hwnd, "LycRomanise Lyrics")
        self._set_icon(hwnd)
        u.ShowWindow(hwnd, SW_SHOWNA)
        self._hwnd = hwnd
        log.info("true transparency attached to window %s (exstyle %#x -> %#x, owner %s, taskbar button %s)",
                 hwnd, self._style_before & 0xFFFFFFFF, style & 0xFFFFFFFF, self._owner,
                 "on" if self.taskbar else "off")
        return True

    def _set_icon(self, hwnd):
        """Give the window the app's .ico (used for its taskbar button and Alt+Tab)."""
        try:
            import config
            if not os.path.isfile(config.ICON_PATH):
                return
            u, ct = self._ulw, self._ct
            v = ct.c_void_p
            u.LoadImageW.restype = v
            u.LoadImageW.argtypes = (v, ct.c_wchar_p, ct.c_uint, ct.c_int, ct.c_int, ct.c_uint)
            u.SendMessageW.restype = v
            u.SendMessageW.argtypes = (v, ct.c_uint, v, v)
            for which, metric in ((1, 11), (0, 49)):           # ICON_BIG / ICON_SMALL; SM_CXICON / SM_CXSMICON
                size = u.GetSystemMetrics(metric)
                icon = u.LoadImageW(None, config.ICON_PATH, 1, size, size, 0x10)   # IMAGE_ICON, LR_LOADFROMFILE
                if icon:
                    u.SendMessageW(hwnd, 0x0080, which, icon)                      # WM_SETICON
        except Exception:
            log.warning("couldn't set the strip's window icon", exc_info=True)

    def set_taskbar(self, on):
        """Show or hide the strip's taskbar button while running."""
        on = bool(on)
        if on == self.taskbar:
            return
        self.taskbar = on
        if self._hwnd is None:
            return                                       # not attached yet: _attach will use the flag
        try:
            u, hwnd = self._ulw, self._hwnd
            style = self._api.GetWindowLongW(hwnd, self._winsys.GWL_EXSTYLE)
            u.ShowWindow(hwnd, SW_HIDE)
            if on:
                style |= WS_EX_APPWINDOW
                u.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, None)
                if self._owner:
                    u.DestroyWindow(self._owner)
                self._owner = None
            else:
                style &= ~WS_EX_APPWINDOW
                self._owner = self._make_owner()
                if self._owner:
                    u.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, self._owner)
            self._api.SetWindowLongW(hwnd, self._winsys.GWL_EXSTYLE, style)
            u.ShowWindow(hwnd, SW_SHOWNA)
            log.info("taskbar button %s", "on" if on else "off")
        except Exception:
            log.exception("couldn't change the taskbar button")

    def _make_owner(self):
        h = self._ulw.CreateWindowExW(WS_EX_TOOLWINDOW, "STATIC", "LycRomanise owner", WS_POPUP,
                                      0, 0, 0, 0, None, None, None, None)
        if not h:
            log.warning("couldn't create the owner window (error %s): the strip may show a taskbar button",
                        self._ct.get_last_error())
        return h

    def detach(self):
        """Back to a normal window (the caller then re-applies the colour key)."""
        try:
            if self._hwnd:
                self._ulw.ShowWindow(self._hwnd, SW_HIDE)
                self._ulw.SetWindowLongPtrW(self._hwnd, GWLP_HWNDPARENT, None)
                self._api.SetWindowLongW(self._hwnd, self._winsys.GWL_EXSTYLE, self._style_before)
                self._ulw.ShowWindow(self._hwnd, SW_SHOWNA)
            if self._owner:
                self._ulw.DestroyWindow(self._owner)
        except Exception:
            log.exception("couldn't undo the window changes")
        self._free()
        self._hwnd = self._owner = None

    def _reset_layered(self):
        """Clear and set the layered style again (see _attach). Used when UpdateLayeredWindow refuses."""
        if self._hwnd is None:
            return
        style = self._api.GetWindowLongW(self._hwnd, self._winsys.GWL_EXSTYLE)
        self._api.SetWindowLongW(self._hwnd, self._winsys.GWL_EXSTYLE, style & ~self._winsys.WS_EX_LAYERED)
        self._api.SetWindowLongW(self._hwnd, self._winsys.GWL_EXSTYLE, style | self._winsys.WS_EX_LAYERED)

    def _free(self):
        if self._dc:
            if self._old:
                self._gdi.SelectObject(self._dc, self._old)
            self._gdi.DeleteDC(self._dc)
        if self._dib:
            self._gdi.DeleteObject(self._dib)
        self._dc = self._dib = self._bits = self._old = self._dib_size = None

    def _ensure_dib(self, w, h):
        if self._dib_size == (w, h):
            return
        self._free()
        ct = self._ct
        screen = self._ulw.GetDC(None)
        try:
            self._dc = self._gdi.CreateCompatibleDC(screen)
            bih = self._BIH(40, w, -h, 1, 32, 0, 0, 0, 0, 0, 0)       # 32-bit, top-down
            bits = ct.c_void_p()
            self._dib = self._gdi.CreateDIBSection(screen, ct.byref(bih), 0, ct.byref(bits), None, 0)
            if not self._dib or not bits.value:
                raise OSError("CreateDIBSection failed (error %s)" % ct.get_last_error())
            self._bits = bits.value
            self._old = self._gdi.SelectObject(self._dc, self._dib)
            self._dib_size = (w, h)
        finally:
            self._ulw.ReleaseDC(None, screen)

    # --- push a frame ---------------------------------------------------------

    def _push(self, size, origin, blend):
        ct = self._ct
        screen = self._ulw.GetDC(None)
        try:
            ok = self._ulw.UpdateLayeredWindow(
                self._hwnd, screen, None, ct.byref(size), self._dc, ct.byref(origin),
                0, ct.byref(blend), 2)                              # ULW_ALPHA
            self._last_error = 0 if ok else ct.get_last_error()     # read before ReleaseDC can overwrite it
            return ok
        finally:
            self._ulw.ReleaseDC(None, screen)

    def commit(self):
        """Show the finished frame. Sets self.failed after repeated errors so the app can fall back."""
        if self.failed or self.img is None:
            return
        try:
            if self._hwnd is None and not self._attach():
                return                                   # window not on screen yet: try next frame
            w, h = self.img.size
            self._ensure_dib(w, h)
            data = self.img.convert("RGBa").tobytes("raw", "BGRa")     # premultiplied BGRA
            ct = self._ct
            ct.memmove(self._bits, data, len(data))
            size, origin = self._wt.SIZE(w, h), self._wt.POINT(0, 0)
            blend = self._BLEND(0, 0, 255, 1)                          # AC_SRC_OVER, per-pixel alpha
            ok = self._push(size, origin, blend)
            if not ok and self._last_error == 87 and not self._reset_tried:
                # "invalid parameter": the window still carries an old SetLayeredWindowAttributes call.
                self._reset_tried = True
                log.info("true transparency: resetting the layered window style and trying again")
                self._reset_layered()
                ok = self._push(size, origin, blend)
            if not ok:
                raise OSError("UpdateLayeredWindow failed (error %s)" % self._last_error)
            self._errors = 0
            self._reset_tried = False
        except Exception:
            self._errors += 1
            if self._errors == 1:
                log.exception("true transparency frame failed")
            if self._errors >= 5:
                log.error("true transparency keeps failing; going back to the normal strip")
                self.failed = True
