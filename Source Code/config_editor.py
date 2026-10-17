"""LycRomanise — appearance settings editor.

A small standalone GUI for changing how the Desktop Lyrics strip looks:
the karaoke colors, the text outline colour, the "next line" color, the strip's
size, this song's lyric timing, and the web view / OBS options — without touching JSON by hand.

Normally opened from the tray icon
("Settings…"). It can also run on its own: python config_editor.py

Every change is saved to appearance.json as you make it, and a running
strip notices the file change and applies it live (within half a second),
so there is no Save-and-restart step.
"""

import logging
import os
import time
import tkinter as tk
from tkinter import colorchooser, messagebox
from tkinter import font as tkfont

import config
import winsys
from version import __version__

log = logging.getLogger("lycro.settings")


BG = "#15141f"          # window
CARD = "#1e1d2b"        # a group of settings
FIELD = "#2a2840"       # inputs and secondary buttons
FIELD_BG = FIELD
FG = "#f2f0ff"
FG_DIM = "#a9a4d0"
ACCENT = "#7c6cf0"      # same purple as the update window
ACCENT_HI = "#9486ff"
FONT = "Segoe UI"
TIMING_STEP_MS = 100     # one step on the Timing tab


def _set_window_icon(win):
    """Use the app's .ico for this window (title bar / taskbar) on Windows."""
    if not winsys.is_windows() or not os.path.exists(config.ICON_PATH):
        return
    try:
        win.iconbitmap(config.ICON_PATH)
    except Exception:
        log.warning("couldn't set the window icon", exc_info=True)


class Toggle(tk.Checkbutton):
    """A tick box bound to a tk.BooleanVar. Ticking or unticking it applies the change immediately."""

    def __init__(self, parent, var, command=None, bg=CARD):
        super().__init__(parent, variable=var, command=command, bg=bg, activebackground=bg,
                         selectcolor=FIELD, fg=FG, activeforeground=FG, highlightthickness=0, bd=0,
                         cursor="hand2", takefocus=1, padx=0, pady=0)


def _label(parent, text, size=10, color=FG, bold=False, bg=CARD, **kw):
    return tk.Label(parent, text=text, fg=color, bg=bg, anchor="w", justify="left",
                    font=(FONT, size, "bold" if bold else "normal"), **kw)


class ColorRow(tk.Frame):
    """One "<label>   [swatch] #rrggbb" row with a click-to-pick swatch."""

    def __init__(self, parent, label, initial_rgb, on_change=None):
        super().__init__(parent, bg=CARD)
        self.rgb = list(initial_rgb)
        self.on_change = on_change
        _label(self, label).pack(side="left")
        self.hex_label = tk.Label(self, text=self._hex(), fg=FG_DIM, bg=CARD, font=("Consolas", 10))
        self.hex_label.pack(side="right")
        self.swatch = tk.Label(self, bg=self._hex(), width=4, height=1, relief="flat", cursor="hand2",
                               highlightthickness=1, highlightbackground=FIELD)
        self.swatch.pack(side="right", padx=(0, 10))
        self.swatch.bind("<Button-1>", self._pick)

    def _hex(self):
        return "#%02x%02x%02x" % tuple(max(0, min(255, c)) for c in self.rgb)

    def _pick(self, _event=None):
        # parent = this settings window: without it the dialog belongs to the (hidden-style) strip and can
        # open behind the always-on-top settings window
        rgb, hex_code = colorchooser.askcolor(color=self._hex(), title="Choose a color",
                                              parent=self.winfo_toplevel())
        if rgb is None:
            return
        self.rgb = [int(c) for c in rgb]
        self.swatch.configure(bg=hex_code)
        self.hex_label.configure(text=hex_code)
        if self.on_change:
            self.on_change()


def _spinbox(parent, var, from_, to, increment=1, width=7):
    return tk.Spinbox(parent, from_=from_, to=to, increment=increment, textvariable=var, width=width,
                      bg=FIELD, fg=FG, buttonbackground=FIELD, insertbackground=FG, relief="flat",
                      highlightthickness=1, highlightbackground=FIELD, highlightcolor=ACCENT,
                      font=("Consolas", 10))


class SizeRow(tk.Frame):
    """One "<label>   [number]" row for an integer setting."""

    def __init__(self, parent, label, initial_value, from_, to, on_change=None, unit="", step=1):
        super().__init__(parent, bg=CARD)
        self.var = tk.IntVar(value=initial_value)
        self.last_good = initial_value
        self.lo, self.hi = from_, to
        self._after = None
        if on_change:
            # Typing "1000" passes through 1, 10 and 100: applying each of those made the strip jump to its
            # smallest size and back. The change is applied once typing pauses (a spin-button click is
            # applied at once, a typed value when the field loses focus or Enter is pressed).
            self.var.trace_add("write", lambda *_: self._soon(on_change))
        _label(self, label).pack(side="left")
        if unit:
            _label(self, unit, size=9, color=FG_DIM).pack(side="right", padx=(6, 0))
        self.spin = _spinbox(self, self.var, from_, to, step)
        self.spin.pack(side="right")
        if on_change:
            self.spin.bind("<FocusOut>", lambda _e: self._now(on_change), add="+")
            self.spin.bind("<Return>", lambda _e: self._now(on_change), add="+")

    def _soon(self, on_change, delay_ms=600):
        try:
            if self._after is not None:
                self.after_cancel(self._after)
            self._after = self.after(delay_ms, lambda: self._now(on_change))
        except tk.TclError:
            pass

    def _now(self, on_change):
        try:
            if self._after is not None:
                self.after_cancel(self._after)
        except tk.TclError:
            pass
        self._after = None
        on_change()

    def get(self):
        """The value, clamped; half-typed text ("", "-") keeps the last good value."""
        try:
            self.last_good = max(self.lo, min(self.hi, int(self.var.get())))
        except (tk.TclError, ValueError):
            pass
        return self.last_good


PREVIEW_TEXT = "Thanks for using LycRomanise"
PREVIEW_LINES = (PREVIEW_TEXT, "Enjoy the lyrics")   # the sample alternates between these, like a song's lines
PREVIEW_H = 74
# These follow the real strip (app.py): same slide time, same easing, same karaoke edge, same reveal.
PREVIEW_ANIM_S = 0.28          # app.LINE_ANIM_SECONDS: the rise from the next-line slot to the top slot
PREVIEW_FILL_S = 2.4           # how long the colour takes to sweep across a line
PREVIEW_LINE_S = 3.0           # time each line stays on top before the next one rises
PREVIEW_TOP_PT, PREVIEW_NEXT_PT = 17, 12   # 25 / 20 pt on the strip, scaled to fit the sample
PREVIEW_TOP_Y, PREVIEW_NEXT_Y = 25, 56
PREVIEW_TICK_MS = 16


class ConfigEditor:
    TABS = (("visuals", "Visuals"), ("window", "Window"), ("timing", "Timing"), ("web", "Web and OBS"))

    def __init__(self, root, on_apply=None, on_reset_position=None, timing=None):
        self.root = root
        self.on_reset_position = on_reset_position
        self.on_apply = on_apply     # optional: called right after each save (the app also watches the file)
        self.timing = timing         # the running app (timing_info / timing_nudge / ...), or None when run alone
        self._built = False
        self._pages = {}
        self._tab_buttons = {}
        self._anim_after = None
        self._anim_t0 = time.perf_counter()
        self._anim_font = tkfont.Font(family=FONT, size=PREVIEW_TOP_PT, weight="bold")
        self._anim_next_font = tkfont.Font(family=FONT, size=PREVIEW_NEXT_PT, weight="bold")
        self._anim_colors = None
        self._anim_widths = {}
        self.root.title("LycRomanise v%s — Settings" % __version__)
        self.root.configure(bg=BG)
        _set_window_icon(self.root)

        settings = config.load_appearance()

        self._build_buttons()          # bottom buttons first so they always stay visible
        self._build_header()
        self._build_tabbar()
        self._body = tk.Frame(self.root, bg=BG)
        self._body.pack(fill="both", expand=True)
        for key, _title in self.TABS:
            self._pages[key] = tk.Frame(self._body, bg=BG)

        self._build_visuals(self._pages["visuals"], settings)
        self._build_window(self._pages["window"], settings)
        self._build_timing(self._pages["timing"])
        self._build_web(self._pages["web"], settings)

        self._built = True
        self._current_tab = None
        self._fit_window()
        self._show_tab("visuals")
        self._rebuild_preview()
        self._tick_preview()
        self._poll_timing()
        self.root.bind("<Destroy>", self._on_destroy, add="+")
        self._present()

    def _on_destroy(self, event):
        if event.widget is self.root and self._anim_after is not None:
            try:
                self.root.after_cancel(self._anim_after)
            except tk.TclError:
                pass
            self._anim_after = None

    def _present(self):
        """Work out the window's final size while it is still hidden, centre it, then show it."""
        try:
            self.root.update_idletasks()
            w, h = self.root.winfo_reqwidth(), self.root.winfo_reqheight()
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            h = min(h, sh - 80)
            self.root.geometry("%dx%d+%d+%d" % (w, h, max(0, (sw - w) // 2), max(0, (sh - h) // 3)))
            self.root.update_idletasks()
        except Exception:
            log.warning("couldn't place the settings window", exc_info=True)
        try:
            self.root.deiconify()
        except Exception:
            pass

    # ------------------------------------------------------------ pieces --

    def _build_header(self):
        head = tk.Frame(self.root, bg=BG)
        head.pack(fill="x", padx=18, pady=(16, 6))
        tk.Label(head, text="Settings", fg=FG, bg=BG, font=(FONT, 16, "bold")).pack(anchor="w")
        tk.Label(head, text="Every change is saved and applied the moment you make it.",
                 fg=FG_DIM, bg=BG, font=(FONT, 9)).pack(anchor="w", pady=(0, 10))
        self.preview_canvas = tk.Canvas(head, height=PREVIEW_H, bg="#000000", highlightthickness=1,
                                        highlightbackground=FIELD)
        self.preview_canvas.pack(fill="x")
        self.preview_canvas.bind("<Configure>", lambda _e: self._rebuild_preview())
        tk.Label(head, text="what lyrics will look like ^", fg=FG_DIM, bg=BG,
                 font=(FONT, 8)).pack(pady=(3, 0))

    def _build_tabbar(self):
        bar = tk.Frame(self.root, bg=BG)
        bar.pack(fill="x", padx=18, pady=(6, 8))
        for key, title in self.TABS:
            b = tk.Label(bar, text=title, bg=BG, fg=FG_DIM, font=(FONT, 10, "bold"), padx=12, pady=5,
                         cursor="hand2")
            b.pack(side="left", padx=(0, 4))
            b.bind("<Button-1>", lambda _e, k=key: self._show_tab(k))
            self._tab_buttons[key] = b

    def _show_tab(self, key):
        for k, page in self._pages.items():
            page.pack_forget()
            self._tab_buttons[k].configure(bg=BG, fg=FG_DIM)
        self._pages[key].pack(fill="x", padx=18, pady=(0, 10))
        self._tab_buttons[key].configure(bg=ACCENT, fg="white")
        self._current_tab = key

    def _card(self, page, title, note=None):
        """A titled group of settings; returns the frame to put rows in."""
        outer = tk.Frame(page, bg=CARD, padx=16, pady=10)
        outer.pack(fill="x", pady=(0, 8))
        if title:
            _label(outer, title, size=11, bold=True).pack(fill="x")
        if note:
            _label(outer, note, size=9, color=FG_DIM, wraplength=430).pack(fill="x", pady=(2, 0))
        inner = tk.Frame(outer, bg=CARD)
        inner.pack(fill="x", pady=(6 if title else 0, 0))
        return inner

    def _toggle(self, card, text, var, hint=None):
        row = tk.Frame(card, bg=CARD)
        row.pack(fill="x", pady=4)
        Toggle(row, var, self._changed).pack(side="right", padx=(12, 0))
        left = tk.Frame(row, bg=CARD)
        left.pack(side="left", fill="x", expand=True)
        title = _label(left, text, wraplength=360)
        title.pack(fill="x")
        title.bind("<Button-1>", lambda _e, v=var: (v.set(not v.get()), self._changed()))   # the text ticks it too
        if hint:
            _label(left, hint, size=9, color=FG_DIM, wraplength=360).pack(fill="x")
        return row

    def _button(self, parent, text, command, primary=False):
        return tk.Button(parent, text=text, command=command, relief="flat", cursor="hand2", padx=12, pady=5,
                         bg=ACCENT if primary else FIELD, fg="white" if primary else FG,
                         activebackground=ACCENT_HI if primary else "#3a3566", activeforeground="white",
                         disabledforeground=FG_DIM, font=(FONT, 10, "bold" if primary else "normal"))

    # ------------------------------------------------------------ tabs --

    def _build_visuals(self, page, st):
        card = self._card(page, "Colours")
        self.unsung_row = ColorRow(card, "Not yet sung", st["karaoke_unsung_rgb"], self._changed)
        self.sung_row = ColorRow(card, "Already sung", st["karaoke_sung_rgb"], self._changed)
        self.next_row = ColorRow(card, "Next line", st["next_line_rgb"], self._changed)
        self.outline_row = ColorRow(card, "Text outline", st["outline_rgb"], self._changed)
        for row in (self.unsung_row, self.sung_row, self.next_row, self.outline_row):
            row.pack(fill="x", pady=4)
        card = self._card(page, "Title card")
        self.title_var = tk.BooleanVar(value=bool(st["show_title_card"]))
        self._toggle(card, "Show 'Artist - Song' first", self.title_var,
                     "For about 10 seconds, or until the first lyric starts. It uses the 'Already sung' and outline colours.")

    def _build_window(self, page, st):
        card = self._card(page, "Strip size")
        self.width_row = SizeRow(card, "Width", st["desktop_width"], 300, 2000, self._changed, "px")
        self.height_row = SizeRow(card, "Height", st["desktop_height"], 80, 500, self._changed, "px")
        self.width_row.pack(fill="x", pady=4)
        self.height_row.pack(fill="x", pady=4)
        card = self._card(page, "Strip")
        self.hide_lock_var = tk.BooleanVar(value=bool(st["hide_lock_icon"]))
        self.taskbar_var = tk.BooleanVar(value=bool(st["show_in_taskbar"]))
        self._toggle(card, "Hide the lock button", self.hide_lock_var, "Lock and unlock from the tray menu only.")
        self._toggle(card, "Show a taskbar button", self.taskbar_var, "Helps OBS and other tools find the window.")
        self._button(card, "Reset lyrics position", self._reset_position).pack(anchor="w", pady=(8, 0))

    def _build_timing(self, page):
        card = self._card(page, "Lyrics timing for this song",
                          "Use this when the lyrics run early or late. It is remembered for this song. "
                          "Each step is 0.1 seconds. Plus shows the lyrics earlier, minus shows them later.")
        self.timing_song = _label(card, "", size=9, color=FG_DIM, wraplength=430)
        self.timing_song.pack(fill="x", pady=(0, 6))
        row = tk.Frame(card, bg=CARD)
        row.pack(pady=(4, 0))
        self._timing_buttons = []
        for text, steps in (("-10", -10), ("-1", -1)):
            self._timing_button(row, text, steps).pack(side="left", padx=4)
        self.timing_value = tk.Label(row, text="0", fg=FG, bg=CARD, width=5, font=(FONT, 30, "bold"))
        self.timing_value.pack(side="left", padx=8)
        for text, steps in (("+1", 1), ("+10", 10)):
            self._timing_button(row, text, steps).pack(side="left", padx=4)
        self.timing_ms = _label(card, "", size=9, color=FG_DIM)
        self.timing_ms.configure(anchor="center", justify="center")
        self.timing_ms.pack(fill="x", pady=(2, 8))
        self.timing_reset_btn = self._button(card, "Reset offset to default", self._timing_reset)
        self.timing_reset_btn.pack(pady=(0, 2))
        self._timing_buttons.append(self.timing_reset_btn)
        card = self._card(page, "Wrong or missing lyrics?")
        self.timing_lookup_btn = self._button(card, "Look up lyrics again", self._timing_lookup)
        self.timing_lookup_btn.pack(anchor="w")
        self._timing_buttons.append(self.timing_lookup_btn)
        self._timing_shown = None

    def _timing_button(self, parent, text, steps):
        b = self._button(parent, text, lambda s=steps: self._timing_nudge(s))
        b.configure(width=4, font=(FONT, 11, "bold"))
        self._timing_buttons.append(b)
        return b

    def _timing_info(self):
        if self.timing is None:
            return None, 0
        try:
            return self.timing.timing_info()
        except Exception:
            log.exception("couldn't read the song timing")
            return None, 0

    def _timing_nudge(self, steps):
        if self.timing is not None:
            self.timing.timing_nudge(steps * TIMING_STEP_MS)
            self._poll_timing(again=False)

    def _timing_reset(self):
        if self.timing is not None:
            self.timing.timing_reset()
            self._poll_timing(again=False)

    def _timing_lookup(self):
        if self.timing is not None:
            self.timing.timing_lookup_again()

    def _poll_timing(self, again=True):
        """Keep the Timing tab in step with the song (it can change while Settings is open)."""
        try:
            if not self.root.winfo_exists():
                return
            name, ms = self._timing_info()
            if (name, ms) != self._timing_shown:
                self._timing_shown = (name, ms)
                steps = ms / float(TIMING_STEP_MS)
                self.timing_value.configure(text=("%+d" % round(steps)) if ms else "0")
                if name is None:
                    self.timing_song.configure(text="Nothing is playing right now.")
                    self.timing_ms.configure(text="")
                else:
                    self.timing_song.configure(text=name)
                    self.timing_ms.configure(text=("%+d ms" % ms) if ms else "default timing")
                for b in self._timing_buttons:
                    b.configure(state="normal" if name is not None else "disabled")
            if again:
                self.root.after(400, self._poll_timing)
        except tk.TclError:
            pass

    def _build_web(self, page, st):
        card = self._card(page, "Web view",
                          "Shows the lyrics in any browser on your network: a phone or tablet, or an OBS Browser "
                          "source. Windows may ask to allow LycRomanise through the firewall (allow Private networks).")
        self.mobile_var = tk.BooleanVar(value=bool(st["mobile_enabled"]))
        self._toggle(card, "Enable web view for web and OBS", self.mobile_var)
        self.port_row = SizeRow(card, "Port", st["mobile_port"], 1024, 65535, self._changed)
        self.port_row.pack(fill="x", pady=4)
        self.url_var = tk.StringVar(value=self._web_url())
        self.url_entry = tk.Entry(card, textvariable=self.url_var, state="readonly", font=("Consolas", 11),
                                  justify="center", bg=FIELD, fg=FG, readonlybackground=FIELD, relief="flat",
                                  highlightthickness=1, highlightbackground=FIELD)
        self.url_entry.pack(fill="x", pady=(6, 6), ipady=5)
        btns = tk.Frame(card, bg=CARD)
        btns.pack(anchor="w")
        self._button(btns, "Copy URL", self._copy_url).pack(side="left")
        self._button(btns, "Show QR code", self._show_qr).pack(side="left", padx=(8, 0))
        card = self._card(page, "OBS text file",
                          "LycRomanise.txt always says what is playing. In OBS add a Text (GDI+) source and tick 'Read from file'.")
        self.np_var = tk.BooleanVar(value=bool(st["now_playing_file"]))
        self._toggle(card, "Keep LycRomanise.txt up to date", self.np_var)
        row = tk.Frame(card, bg=CARD)
        row.pack(fill="x", pady=(2, 0))
        _label(row, "Text").pack(side="left")
        import nowplaying
        self._np_labels = nowplaying.FORMAT_LABELS
        self.np_format_var = tk.StringVar(value=self._np_labels.get(st["now_playing_format"], "Artist - Song"))
        menu = tk.OptionMenu(row, self.np_format_var, *self._np_labels.values(), command=lambda _v: self._changed())
        menu.configure(bg=FIELD, fg=FG, activebackground=ACCENT, activeforeground="white", relief="flat",
                       highlightthickness=0, bd=0, font=(FONT, 10), cursor="hand2")
        menu["menu"].configure(bg=FIELD, fg=FG, activebackground=ACCENT, activeforeground="white", bd=0)
        menu.pack(side="right")
        btns = tk.Frame(card, bg=CARD)
        btns.pack(anchor="w", pady=(6, 0))
        self._button(btns, "Copy path", self._copy_np_path).pack(side="left")
        self._button(btns, "Open folder", self._open_np_folder).pack(side="left", padx=(8, 0))
        card = self._card(page, None)
        self.hide_strip_var = tk.BooleanVar(value=bool(st["hide_desktop_strip"]))
        self._toggle(card, "Hide the lyrics on this PC", self.hide_strip_var,
                     "Only show them in the web view / OBS. Takes effect while the web view is enabled.")

    def _web_url(self):
        try:
            import mobile_server
            return mobile_server.mobile_url(self.port_row.get())
        except Exception:
            return ""

    def _refresh_url(self):
        url = self._web_url()
        if url != self.url_var.get():
            self.url_var.set(url)

    def _copy_url(self):
        try:
            self.root.clipboard_clear()
            self.root.clipboard_append(self.url_var.get())
            self.root.update()
        except Exception:
            log.exception("couldn't copy the address")

    def _show_qr(self):
        try:
            import mobile_dialog
            self._save()                  # make sure the latest port is saved/applied first
            mobile_dialog.show_qr(self.root, self.port_row.get())
        except Exception:
            log.exception("couldn't show the QR code")

    # ------------------------------------------------------------ layout --

    def _build_buttons(self):
        btns = tk.Frame(self.root, bg=BG)
        btns.pack(fill="x", padx=18, pady=(4, 16), side="bottom")
        self._button(btns, "Done", self._done, primary=True).pack(side="right")
        self._button(btns, "Reset to defaults", self._reset).pack(side="left")

    def _fit_window(self):
        """Size the body from the tallest tab, so switching tabs never resizes the window (no scrolling)."""
        try:
            self.root.update_idletasks()
            want_h = want_w = 0
            for page in self._pages.values():
                page.pack(fill="x", padx=18, pady=(0, 10))
                self.root.update_idletasks()
                want_h = max(want_h, int(page.winfo_reqheight()) + 10)
                want_w = max(want_w, int(page.winfo_reqwidth()) + 36)
                page.pack_forget()
            self._body.configure(width=max(want_w, 500), height=want_h)
            self._body.pack_propagate(False)
        except Exception:
            log.warning("couldn't size the settings window from its content", exc_info=True)

    # ------------------------------------------------------------ preview --

    def _hex(self, row):
        return "#%02x%02x%02x" % tuple(max(0, min(255, c)) for c in row.rgb)

    def _rebuild_preview(self):
        """Colours or width changed: pick the new colours up. The sample keeps running where it was."""
        c = getattr(self, "preview_canvas", None)
        if c is None or not hasattr(self, "outline_row"):
            return
        self._anim_colors = {
            "sung": tuple(self.sung_row.rgb), "unsung": tuple(self.unsung_row.rgb),
            "next": self._hex(self.next_row), "outline": self._hex(self.outline_row)}

    def _prefix(self, font, text):
        """Cumulative widths of text[:i], cached per (text, font size), as the strip's TextMeter does."""
        key = (text, int(font.cget("size")))
        got = self._anim_widths.get(key)
        if got is None:
            got = [font.measure(text[:i]) for i in range(len(text) + 1)]
            self._anim_widths[key] = got
        return got

    def _preview_line(self, c, text, font, cx, cy, fraction, colors):
        """One line with a dark outline; yellow turns to pink left to right with a soft edge, like the strip."""
        prefix = self._prefix(font, text)
        total = prefix[-1]
        start_x = cx - total / 2.0
        fill_x = fraction * total
        gradient_px = max(10.0, total / max(1, len(text)) * 2.2)
        outline = colors["outline"]
        sung, unsung = colors["sung"], colors["unsung"]
        for i, ch in enumerate(text):
            if ch == " ":
                continue
            x = start_x + prefix[i]
            for dx, dy in ((-1.5, 0), (1.5, 0), (0, -1.5), (0, 1.5), (-1, -1), (1, 1), (-1, 1), (1, -1)):
                c.create_text(x + dx, cy + dy, text=ch, fill=outline, anchor="w", font=font)
            mid = (prefix[i] + prefix[i + 1]) / 2.0
            t = max(0.0, min(1.0, (mid - fill_x) / gradient_px))
            rgb = tuple(max(0, min(255, int(sung[k] + (unsung[k] - sung[k]) * t))) for k in range(3))
            c.create_text(x, cy, text=ch, fill="#%02x%02x%02x" % rgb, anchor="w", font=font)

    def _tick_preview(self):
        """The sample plays the strip's line change: the next line rises into the top slot while growing,
        the colour sweeps across it, and the line after fades up underneath. Then the lines swap."""
        try:
            if not self.root.winfo_exists():
                return
            c = self.preview_canvas
            if self._anim_colors is None:
                self._rebuild_preview()
            colors = self._anim_colors
            if colors is not None:
                el = time.perf_counter() - self._anim_t0
                k = int(el // PREVIEW_LINE_S)
                t_line = el - k * PREVIEW_LINE_S
                top_text = PREVIEW_LINES[k % len(PREVIEW_LINES)]
                next_text = PREVIEW_LINES[(k + 1) % len(PREVIEW_LINES)]
                anim_t = min(1.0, t_line / PREVIEW_ANIM_S)
                eased = 1 - (1 - anim_t) ** 3
                cx = (c.winfo_width() or 460) / 2.0
                c.delete("all")
                # the rising line: from the next-line slot and size to the top slot and size
                y = PREVIEW_NEXT_Y + (PREVIEW_TOP_Y - PREVIEW_NEXT_Y) * eased
                size = round(PREVIEW_NEXT_PT + (PREVIEW_TOP_PT - PREVIEW_NEXT_PT) * eased)
                self._anim_font.configure(size=max(PREVIEW_NEXT_PT, size))
                self._preview_line(c, top_text, self._anim_font, cx, y, min(1.0, t_line / PREVIEW_FILL_S), colors)
                # the next line appears once the rising line has cleared its slot
                reveal = max(0.0, min(1.0, (anim_t - 0.35) / 0.65))
                if reveal > 0.0:
                    ny = PREVIEW_NEXT_Y + (1.0 - (1 - (1 - reveal) ** 3)) * 8
                    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                        c.create_text(cx + dx, ny + dy, text=next_text, fill=colors["outline"], anchor="center",
                                      font=self._anim_next_font)
                    c.create_text(cx, ny, text=next_text, fill=colors["next"], anchor="center",
                                  font=self._anim_next_font)
            self._anim_after = self.root.after(PREVIEW_TICK_MS, self._tick_preview)
        except tk.TclError:
            self._anim_after = None

    def _draw_preview(self):
        """Colours changed: rebuild the sample so every colour is current."""
        self._rebuild_preview()

    def _collect(self):
        return {
            "karaoke_unsung_rgb": self.unsung_row.rgb,
            "karaoke_sung_rgb": self.sung_row.rgb,
            "next_line_rgb": self.next_row.rgb,
            "desktop_width": self.width_row.get(),
            "desktop_height": self.height_row.get(),
            "show_title_card": bool(self.title_var.get()),
            "hide_lock_icon": bool(self.hide_lock_var.get()),
            "show_in_taskbar": bool(self.taskbar_var.get()),
            "outline_rgb": self.outline_row.rgb,
            "hide_desktop_strip": bool(self.hide_strip_var.get()),
            "mobile_enabled": bool(self.mobile_var.get()),
            "mobile_port": self.port_row.get(),
            "now_playing_file": bool(self.np_var.get()),
            "now_playing_format": self._np_format_key(),
        }

    def _reset_position(self):
        """Move the strip back to its default place (live when the app is running)."""
        try:
            if self.on_reset_position:
                self.on_reset_position()
            else:
                config.reset_window_position()
                messagebox.showinfo("Lyrics position", "The position was reset. It applies the next time LycRomanise starts.")
        except Exception:
            log.exception("couldn't reset the position")
            messagebox.showerror("Lyrics position", "Couldn't reset the position. See logs/lycromanise.log.")

    def _copy_np_path(self):
        try:
            path = config.now_playing_path()
            self.root.clipboard_clear()
            self.root.clipboard_append(path)
            self.root.update()
        except Exception:
            log.exception("couldn't copy the path")

    def _open_np_folder(self):
        try:
            path = config.now_playing_path()
            folder = os.path.dirname(os.path.abspath(path))
            os.makedirs(folder, exist_ok=True)
            if winsys.is_windows():
                os.startfile(folder)      # noqa: S606 - Explorer on our own data folder
        except Exception:
            log.exception("couldn't open the folder")
            messagebox.showerror("OBS text file", "Couldn't open the folder. See logs/lycromanise.log.")

    def _np_format_key(self):
        shown = self.np_format_var.get()
        for key, label in self._np_labels.items():
            if label == shown:
                return key
        return "artist_title"

    def _changed(self, *_):
        """Something was edited: refresh the sample, then save and apply it to the strip straight away."""
        if not getattr(self, "_built", False):
            return      # initial values being filled in while the window is built: nothing to save
        try:
            if not self.root.winfo_exists():
                return  # a delayed change arriving after the window was closed
        except tk.TclError:
            return
        self._draw_preview()
        self._refresh_url()
        self._save()

    def _save(self):
        try:
            # Merge into the current file so settings this window doesn't show
            # (spawn height, max line length, global lyric offset...) survive a save.
            merged = config.load_appearance()
            merged.update(self._collect())
            config.save_appearance(merged)
        except OSError as exc:
            log.error("couldn't write appearance.json: %s", exc)
            messagebox.showerror("Couldn't save", f"Failed to write appearance.json:\n{exc}")
            return
        if self.on_apply:
            try:
                self.on_apply()
            except Exception:
                log.exception("applying settings failed")

    def _done(self):
        try:
            self._save()          # a number typed a moment ago may not have been applied yet
        except Exception:
            log.exception("couldn't save on closing")
        self.root.destroy()

    def _reset(self):
        if not messagebox.askyesno(
            "Reset to defaults", "Discard your changes and reset every setting to default?"
        ):
            return
        defaults = config.DEFAULT_APPEARANCE
        self.unsung_row.rgb = list(defaults["karaoke_unsung_rgb"])
        self.sung_row.rgb = list(defaults["karaoke_sung_rgb"])
        self.next_row.rgb = list(defaults["next_line_rgb"])
        self.outline_row.rgb = list(defaults["outline_rgb"])
        for row in (self.unsung_row, self.sung_row, self.next_row, self.outline_row):
            row.swatch.configure(bg=row._hex())
            row.hex_label.configure(text=row._hex())
        self.width_row.var.set(defaults["desktop_width"])
        self.height_row.var.set(defaults["desktop_height"])
        self.title_var.set(defaults["show_title_card"])
        self.hide_lock_var.set(defaults["hide_lock_icon"])
        self.taskbar_var.set(defaults["show_in_taskbar"])
        self.mobile_var.set(defaults["mobile_enabled"])
        self.hide_strip_var.set(defaults["hide_desktop_strip"])
        self.port_row.var.set(defaults["mobile_port"])
        self.np_var.set(defaults["now_playing_file"])
        self.np_format_var.set(self._np_labels.get(defaults["now_playing_format"], "Artist - Song"))
        self._draw_preview()
        self._refresh_url()
        self._save()


def main():
    winsys.enable_dpi_awareness()      # before the first Tk window, so text is crisp when scaled
    root = tk.Tk()
    root.withdraw()                    # shown by ConfigEditor once it has its final size
    try:
        import applog
        applog.setup()
    except Exception:
        pass
    ConfigEditor(root)
    root.mainloop()


if __name__ == "__main__":
    main()
