"""LycRomanise — appearance settings editor.

A small standalone GUI for changing how the Desktop Lyrics strip looks:
the karaoke colors, the text outline colour, the "next line" color, font sizes, the strip's
size, and the optional mobile view — without touching JSON by hand.

Normally opened from the tray icon
("Settings…"). It can also run on its own: python config_editor.py

Every change is saved to appearance.json as you make it, and a running
strip notices the file change and applies it live (within half a second),
so there is no Save-and-restart step.
"""

import logging
import os
import tkinter as tk
from tkinter import colorchooser, messagebox

import config
import winsys

log = logging.getLogger("lycro.settings")

APPLY_DELAY_MS = 350   # wait for typing/spinning to settle before saving

BG = "#15141f"          # window
CARD = "#1e1d2b"        # a group of settings
FIELD = "#2a2840"       # inputs and secondary buttons
FIELD_BG = FIELD
FG = "#f2f0ff"
FG_DIM = "#a9a4d0"
ACCENT = "#7c6cf0"      # same purple as the update window
ACCENT_HI = "#9486ff"
FONT = "Segoe UI"


def _set_window_icon(win):
    """Use the app's .ico for this window (title bar / taskbar) on Windows."""
    if not winsys.is_windows() or not os.path.exists(config.ICON_PATH):
        return
    try:
        win.iconbitmap(config.ICON_PATH)
    except Exception:
        log.warning("couldn't set the window icon", exc_info=True)


class Toggle(tk.Canvas):
    """An on/off switch bound to a tk.BooleanVar. Click (or Space) flips it."""
    W, H = 42, 22

    def __init__(self, parent, var, command=None, bg=CARD):
        super().__init__(parent, width=self.W, height=self.H, bg=bg, highlightthickness=0, bd=0,
                         cursor="hand2", takefocus=1)
        self.var, self.command = var, command
        self.bind("<Button-1>", self._flip)
        self.bind("<space>", self._flip)
        var.trace_add("write", lambda *_: self._draw())
        self._draw()

    def _flip(self, _event=None):
        self.var.set(not self.var.get())
        if self.command:
            self.command()

    def _draw(self):
        on = bool(self.var.get())
        self.delete("all")
        r = self.H // 2
        self.create_oval(0, 0, self.H, self.H, fill=ACCENT if on else FIELD, outline="")
        self.create_oval(self.W - self.H, 0, self.W, self.H, fill=ACCENT if on else FIELD, outline="")
        self.create_rectangle(r, 0, self.W - r, self.H, fill=ACCENT if on else FIELD, outline="")
        x = self.W - r - 1 if on else r + 1
        self.create_oval(x - r + 3, 3, x + r - 3, self.H - 3, fill="#ffffff", outline="")


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
        rgb, hex_code = colorchooser.askcolor(color=self._hex(), title="Choose a color")
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

    def __init__(self, parent, label, initial_value, from_, to, on_change=None, unit=""):
        super().__init__(parent, bg=CARD)
        self.var = tk.IntVar(value=initial_value)
        self.last_good = initial_value
        self.lo, self.hi = from_, to
        if on_change:
            self.var.trace_add("write", lambda *_: on_change())
        _label(self, label).pack(side="left")
        if unit:
            _label(self, unit, size=9, color=FG_DIM).pack(side="right", padx=(6, 0))
        _spinbox(self, self.var, from_, to).pack(side="right")

    def get(self):
        """The value, clamped; half-typed text ("", "-") keeps the last good value."""
        try:
            self.last_good = max(self.lo, min(self.hi, int(self.var.get())))
        except (tk.TclError, ValueError):
            pass
        return self.last_good


class ConfigEditor:
    TABS = (("look", "Look"), ("text", "Text & size"), ("behaviour", "Behaviour"), ("mobile", "Phone & OBS"))

    def __init__(self, root, on_apply=None, on_reset_position=None):
        self.root = root
        self.on_reset_position = on_reset_position
        self.on_apply = on_apply     # optional: called right after each save (the app also watches the file)
        self._apply_job = None
        self._built = False
        self._pages = {}
        self._tab_buttons = {}
        self.root.title("LycRomanise v%s — Settings" % __import__("version").__version__)
        self.root.configure(bg=BG)
        _set_window_icon(self.root)

        settings = config.load_appearance()

        # Bottom buttons first so they stay visible however small the window gets.
        self._build_buttons()
        self._build_header()
        self._build_tabbar()
        body = self._make_scroll_area()
        for key, _title in self.TABS:
            page = tk.Frame(body, bg=BG)
            self._pages[key] = page

        self._build_look(self._pages["look"], settings)
        self._build_text(self._pages["text"], settings)
        self._build_behaviour(self._pages["behaviour"], settings)
        self._build_mobile(self._pages["mobile"], settings)

        self._built = True
        self._current_tab = None
        self._fit_window()
        self._show_tab("look")
        self._draw_preview()

    # ------------------------------------------------------------ pieces --

    def _build_header(self):
        head = tk.Frame(self.root, bg=BG)
        head.pack(fill="x", padx=18, pady=(16, 8))
        tk.Label(head, text="Settings", fg=FG, bg=BG, font=(FONT, 16, "bold")).pack(anchor="w")
        tk.Label(head, text="Changes are saved and applied to the strip as you make them.",
                 fg=FG_DIM, bg=BG, font=(FONT, 9)).pack(anchor="w", pady=(0, 10))
        self.preview_canvas = tk.Canvas(head, height=50, bg="#000000", highlightthickness=1,
                                        highlightbackground=FIELD)
        self.preview_canvas.pack(fill="x")

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
        try:
            self._scroll_canvas.yview_moveto(0)
        except Exception:
            pass

    def _card(self, page, title, note=None):
        """A titled group of settings; returns the frame to put rows in."""
        outer = tk.Frame(page, bg=CARD, padx=16, pady=12)
        outer.pack(fill="x", pady=(0, 10))
        _label(outer, title, size=11, bold=True).pack(fill="x")
        if note:
            _label(outer, note, size=9, color=FG_DIM, wraplength=420).pack(fill="x", pady=(2, 0))
        inner = tk.Frame(outer, bg=CARD)
        inner.pack(fill="x", pady=(8, 0))
        return inner

    def _toggle(self, card, text, var, hint=None):
        row = tk.Frame(card, bg=CARD)
        row.pack(fill="x", pady=5)
        Toggle(row, var, self._changed).pack(side="right", padx=(12, 0))
        left = tk.Frame(row, bg=CARD)
        left.pack(side="left", fill="x", expand=True)
        _label(left, text, wraplength=340).pack(fill="x")
        if hint:
            _label(left, hint, size=9, color=FG_DIM, wraplength=340).pack(fill="x")
        return row

    def _button(self, parent, text, command, primary=False):
        return tk.Button(parent, text=text, command=command, relief="flat", cursor="hand2", padx=12, pady=5,
                         bg=ACCENT if primary else FIELD, fg="white" if primary else FG,
                         activebackground=ACCENT_HI if primary else "#3a3566", activeforeground="white",
                         font=(FONT, 10, "bold" if primary else "normal"))

    # ------------------------------------------------------------ tabs --

    def _build_look(self, page, st):
        card = self._card(page, "Colours", "The title card uses the 'Already sung' colour and the text outline colour.")
        self.unsung_row = ColorRow(card, "Not yet sung", st["karaoke_unsung_rgb"], self._changed)
        self.sung_row = ColorRow(card, "Already sung", st["karaoke_sung_rgb"], self._changed)
        self.next_row = ColorRow(card, "Next line", st["next_line_rgb"], self._changed)
        self.outline_row = ColorRow(card, "Text outline", st["outline_rgb"], self._changed)
        for row in (self.unsung_row, self.sung_row, self.next_row, self.outline_row):
            row.pack(fill="x", pady=4)
            row.swatch.bind("<Button-1>", lambda _e: self._draw_preview(), add="+")
        card = self._card(page, "Outline")
        self.outline_var = tk.DoubleVar(value=float(st["outline_size"]))
        self.outline_var.trace_add("write", lambda *_: self._changed())
        row = tk.Frame(card, bg=CARD)
        row.pack(fill="x", pady=4)
        _label(row, "Outline thickness").pack(side="left")
        _label(row, "px", size=9, color=FG_DIM).pack(side="right", padx=(6, 0))
        _spinbox(row, self.outline_var, 0, 4, 0.5).pack(side="right")
        card = self._card(page, "Title card")
        self.title_var = tk.BooleanVar(value=bool(st["show_title_card"]))
        self._toggle(card, "Show 'Artist - Song' first", self.title_var,
                     "For about 10 seconds, or until the first lyric starts.")

    def _build_text(self, page, st):
        card = self._card(page, "Font sizes")
        self.curr_size_row = SizeRow(card, "Current line", st["curr_font_size"], 10, 60, self._changed, "pt")
        self.next_size_row = SizeRow(card, "Next line", st["next_font_size"], 6, 40, self._changed, "pt")
        self.curr_min_row = SizeRow(card, "Current line, smallest", st["curr_font_min_size"], 6, 40, self._changed, "pt")
        self.next_min_row = SizeRow(card, "Next line, smallest", st["next_font_min_size"], 4, 30, self._changed, "pt")
        for row in (self.curr_size_row, self.next_size_row, self.curr_min_row, self.next_min_row):
            row.pack(fill="x", pady=4)
        card = self._card(page, "Strip size")
        self.width_row = SizeRow(card, "Width", st["desktop_width"], 300, 2000, self._changed, "px")
        self.height_row = SizeRow(card, "Height", st["desktop_height"], 80, 500, self._changed, "px")
        self.width_row.pack(fill="x", pady=4)
        self.height_row.pack(fill="x", pady=4)
        card = self._card(page, "Lyrics timing and lines")
        self.offset_row = SizeRow(card, "Lyric offset", st["lyric_offset_ms"], -3000, 3000, self._changed, "ms")
        self.offset_row.pack(fill="x", pady=4)
        _label(card, "Positive: lyrics appear earlier. Negative: later.", size=9, color=FG_DIM).pack(fill="x")
        self.split_row = SizeRow(card, "Split lines longer than", st["max_line_chars"], 0, 200, self._changed, "chars")
        self.split_row.pack(fill="x", pady=(10, 4))
        _label(card, "0 = never split: each line stays whole and keeps the lyrics site's timing. A higher number "
                     "cuts longer lines into pieces; the later pieces' timing is a guess.",
               size=9, color=FG_DIM, wraplength=420).pack(fill="x")

    def _build_behaviour(self, page, st):
        card = self._card(page, "Strip")
        self.hide_lock_var = tk.BooleanVar(value=bool(st["hide_lock_icon"]))
        self.taskbar_var = tk.BooleanVar(value=bool(st["show_in_taskbar"]))
        self._toggle(card, "Hide the lock icon", self.hide_lock_var, "Lock and unlock from the tray menu only.")
        self._toggle(card, "Show a taskbar button", self.taskbar_var, "Helps OBS and other tools find the window.")
        self._button(card, "Reset lyrics position", self._reset_position).pack(anchor="w", pady=(8, 0))
        if winsys.is_windows():
            card = self._card(page, "Windows")
            self.autostart_var = tk.BooleanVar(value=winsys.autostart_enabled())
            self._toggle(card, "Start with Windows", self.autostart_var)
            # this one applies at once instead of through the debounced save
            self.autostart_var.trace_add("write", lambda *_: self._autostart_changed())
        else:
            self.autostart_var = None

    def _build_mobile(self, page, st):
        card = self._card(page, "Phones and tablets",
                          "Show the lyrics on a device on the same Wi-Fi. Windows may ask to allow LycRomanise "
                          "through the firewall: allow it on Private networks.")
        self.mobile_var = tk.BooleanVar(value=bool(st["mobile_enabled"]))
        self._toggle(card, "Show the lyrics on phones and tablets", self.mobile_var)
        self.port_row = SizeRow(card, "Port", st["mobile_port"], 1024, 65535, self._changed)
        self.port_row.pack(fill="x", pady=4)
        self._button(card, "Show address / QR code…", self._show_mobile, primary=True).pack(anchor="w", pady=(8, 0))
        card = self._card(page, "OBS and other screens")
        self.hide_strip_var = tk.BooleanVar(value=bool(st["hide_desktop_strip"]))
        self._toggle(card, "Hide the lyrics on this PC", self.hide_strip_var,
                     "For when you only use the OBS browser source or a phone. Only takes effect while "
                     "'Show the lyrics on phones and tablets' is on.")

    # ------------------------------------------------------------ layout --

    def _build_buttons(self):
        btns = tk.Frame(self.root, bg=BG)
        btns.pack(fill="x", padx=18, pady=(4, 16), side="bottom")
        self._button(btns, "Done", self._done, primary=True).pack(side="right")
        self._button(btns, "Reset to defaults", self._reset).pack(side="left")

    def _make_scroll_area(self):
        """A vertically scrolling body. The window is sized from its content (not a
        fixed pixel size) so it fits at any display scaling, and scrolls when the
        screen is too short to show everything."""
        outer = tk.Frame(self.root, bg=BG)
        outer.pack(fill="both", expand=True)
        self._scroll_canvas = canvas = tk.Canvas(outer, bg=BG, highlightthickness=0, bd=0)
        bar = tk.Scrollbar(outer, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=bar.set)
        bar.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)
        self._scroll_body = inner = tk.Frame(canvas, bg=BG)
        window = canvas.create_window((0, 0), window=inner, anchor="nw")
        inner.bind("<Configure>", lambda _e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfigure(window, width=e.width))
        self.root.bind("<MouseWheel>", lambda e: canvas.yview_scroll(int(-e.delta / 120), "units"))
        return inner

    def _fit_window(self):
        """Size the window from the tallest tab (so switching tabs doesn't resize it), capped to the screen."""
        try:
            self.root.update_idletasks()
            want_h = want_w = 0
            for page in self._pages.values():
                page.pack(fill="x", padx=18, pady=(0, 10))
                self.root.update_idletasks()
                want_h = max(want_h, int(page.winfo_reqheight()) + 10)
                want_w = max(want_w, int(page.winfo_reqwidth()) + 36 + 20)
                page.pack_forget()
            max_h = int(self.root.winfo_screenheight() * 0.78) - 260    # room for header, tabs, buttons, taskbar
            self._scroll_canvas.configure(width=max(want_w, 470), height=max(160, min(want_h, max_h)))
        except Exception:
            log.warning("couldn't size the settings window from its content", exc_info=True)

    def _draw_preview(self):
        # Spinboxes/colour rows fire their change callback while the window is
        # still being built, before the preview canvas exists.
        c = getattr(self, "preview_canvas", None)
        if c is None:
            return
        c.delete("all")
        w = c.winfo_width() or 340
        half = "half-sung, half-not — like the real thing"
        mid = len(half) // 2
        x = 10
        unsung_hex = "#%02x%02x%02x" % tuple(self.unsung_row.rgb)
        sung_hex = "#%02x%02x%02x" % tuple(self.sung_row.rgb)
        outline_hex = "#%02x%02x%02x" % tuple(self.outline_row.rgb)
        for i, ch in enumerate(half):
            color = sung_hex if i < mid else unsung_hex
            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1)):
                c.create_text(x + dx, 22 + dy, text=ch, fill=outline_hex, anchor="w", font=("Segoe UI", 13, "bold"))
            c.create_text(x, 22, text=ch, fill=color, anchor="w", font=("Segoe UI", 13, "bold"))
            x += 9

    def _collect(self):
        return {
            "karaoke_unsung_rgb": self.unsung_row.rgb,
            "karaoke_sung_rgb": self.sung_row.rgb,
            "next_line_rgb": self.next_row.rgb,
            "curr_font_size": self.curr_size_row.get(),
            "next_font_size": self.next_size_row.get(),
            "curr_font_min_size": self.curr_min_row.get(),
            "next_font_min_size": self.next_min_row.get(),
            "desktop_width": self.width_row.get(),
            "desktop_height": self.height_row.get(),
            "lyric_offset_ms": self.offset_row.get(),
            "max_line_chars": self.split_row.get(),
            "show_title_card": bool(self.title_var.get()),
            "hide_lock_icon": bool(self.hide_lock_var.get()),
            "show_in_taskbar": bool(self.taskbar_var.get()),
            "outline_size": self._outline(),
            "outline_rgb": self.outline_row.rgb,
            "hide_desktop_strip": bool(self.hide_strip_var.get()),
            "mobile_enabled": bool(self.mobile_var.get()),
            "mobile_port": self.port_row.get(),
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

    def _show_mobile(self):
        import mobile_dialog
        if self._apply_job is not None:       # make sure the latest port is saved/applied first
            self._save()
        mobile_dialog.show(self.root, self.port_row.get(), bool(self.mobile_var.get()))

    def _outline(self):
        try:
            return max(0.0, min(4.0, float(self.outline_var.get())))
        except (tk.TclError, ValueError):
            return config.DEFAULT_APPEARANCE["outline_size"]

    def _changed(self, *_):
        """Something was edited: refresh the preview and (debounced) save+apply."""
        if not getattr(self, "_built", False):
            return      # initial values being filled in while the window is built: nothing to save
        self._draw_preview()
        if self._apply_job is not None:
            try:
                self.root.after_cancel(self._apply_job)
            except Exception:
                pass
        self._apply_job = self.root.after(APPLY_DELAY_MS, self._save)

    def _autostart_changed(self):
        if getattr(self, "_autostart_busy", False):
            return
        self._autostart_busy = True
        try:
            if not winsys.set_autostart(bool(self.autostart_var.get())):
                self.autostart_var.set(winsys.autostart_enabled())
                messagebox.showerror("Start with Windows", "Couldn't change the setting. See logs/lycromanise.log.")
        finally:
            self._autostart_busy = False
        if self.on_apply:
            try:
                self.on_apply()      # refreshes the tray menu's check mark
            except Exception:
                log.exception("applying settings failed")

    def _save(self):
        self._apply_job = None
        try:
            # Merge into the current file so settings this window doesn't show
            # (spawn height, max line length...) survive a save.
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
        if self._apply_job is not None:
            self._save()
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
        self.curr_size_row.var.set(defaults["curr_font_size"])
        self.next_size_row.var.set(defaults["next_font_size"])
        self.curr_min_row.var.set(defaults["curr_font_min_size"])
        self.next_min_row.var.set(defaults["next_font_min_size"])
        self.width_row.var.set(defaults["desktop_width"])
        self.height_row.var.set(defaults["desktop_height"])
        self.offset_row.var.set(defaults["lyric_offset_ms"])
        self.split_row.var.set(defaults["max_line_chars"])
        self.title_var.set(defaults["show_title_card"])
        self.hide_lock_var.set(defaults["hide_lock_icon"])
        self.taskbar_var.set(defaults["show_in_taskbar"])
        self.outline_var.set(defaults["outline_size"])
        self.mobile_var.set(defaults["mobile_enabled"])
        self.hide_strip_var.set(defaults["hide_desktop_strip"])
        self.port_row.var.set(defaults["mobile_port"])
        self._draw_preview()
        self._save()


def main():
    winsys.enable_dpi_awareness()      # before the first Tk window, so text is crisp when scaled
    root = tk.Tk()
    try:
        import applog
        applog.setup()
    except Exception:
        pass
    ConfigEditor(root)
    root.mainloop()


if __name__ == "__main__":
    main()
