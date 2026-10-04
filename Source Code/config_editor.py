"""LycRomanise — appearance settings editor.

A small standalone GUI for changing how the Desktop Lyrics strip looks:
the karaoke colors, the dim "next line" color, font sizes, and the strip's
size — without touching JSON by hand.

Normally opened from the tray icon
("Settings…"). It can also run on its own: python config_editor.py

Every change is saved to appearance.json as you make it, and a running
strip notices the file change and applies it live (within half a second),
so there is no Save-and-restart step.
"""

import logging
import os
import sys
import tkinter as tk
from tkinter import colorchooser, messagebox

import config
import winsys

log = logging.getLogger("spoti.settings")

APPLY_DELAY_MS = 350   # wait for typing/spinning to settle before saving

BG = "#0d0d0d"
FG = "#e8e8e8"
FG_DIM = "#9a9a9a"
FIELD_BG = "#1a1a1a"
ACCENT = "#1db954"


def _set_window_icon(win):
    """Use the app's .ico for this window (title bar / taskbar) on Windows."""
    if not winsys.is_windows() or not os.path.exists(config.ICON_PATH):
        return
    try:
        win.iconbitmap(config.ICON_PATH)
    except Exception:
        log.warning("couldn't set the window icon", exc_info=True)


class ColorRow(tk.Frame):
    """One "<label>  [swatch]  #rrggbb" row with a click-to-pick swatch."""

    def __init__(self, parent, label, initial_rgb, on_change=None):
        super().__init__(parent, bg=BG)
        self.rgb = list(initial_rgb)
        self.on_change = on_change

        tk.Label(
            self, text=label, fg=FG_DIM, bg=BG, font=("Segoe UI", 10), width=16, anchor="w",
        ).pack(side="left")

        self.swatch = tk.Label(
            self, bg=self._hex(), width=4, height=1, relief="flat", cursor="hand2",
        )
        self.swatch.pack(side="left", padx=(0, 8))
        self.swatch.bind("<Button-1>", self._pick)

        self.hex_label = tk.Label(
            self, text=self._hex(), fg=FG, bg=BG, font=("Consolas", 10),
        )
        self.hex_label.pack(side="left")

    def _hex(self):
        return "#%02x%02x%02x" % tuple(max(0, min(255, c)) for c in self.rgb)

    def _pick(self, _event=None):
        rgb, hex_code = colorchooser.askcolor(
            color=self._hex(), title="Choose a color",
        )
        if rgb is None:
            return
        self.rgb = [int(c) for c in rgb]
        self.swatch.configure(bg=hex_code)
        self.hex_label.configure(text=hex_code)
        if self.on_change:
            self.on_change()


class SizeRow(tk.Frame):
    """One "<label>  [spinbox]" row for an integer setting."""

    def __init__(self, parent, label, initial_value, from_, to, on_change=None):
        super().__init__(parent, bg=BG)
        self.var = tk.IntVar(value=initial_value)
        self.last_good = initial_value
        self.lo, self.hi = from_, to
        if on_change:
            self.var.trace_add("write", lambda *_: on_change())

        tk.Label(
            self, text=label, fg=FG_DIM, bg=BG, font=("Segoe UI", 10), width=16, anchor="w",
        ).pack(side="left")

        tk.Spinbox(
            self, from_=from_, to=to, textvariable=self.var, width=6,
            bg=FIELD_BG, fg=FG, buttonbackground=FIELD_BG, relief="flat",
            font=("Consolas", 10),
        ).pack(side="left")

    def get(self):
        """The spinbox's value, clamped; half-typed text (\"\", \"-\") keeps the last good value."""
        try:
            self.last_good = max(self.lo, min(self.hi, int(self.var.get())))
        except (tk.TclError, ValueError):
            pass
        return self.last_good


class ConfigEditor:
    def __init__(self, root, on_apply=None):
        self.root = root
        self.on_apply = on_apply     # optional: called right after each save (the app also watches the file)
        self._apply_job = None
        self.root.title("LycRomanise v%s — Settings" % __import__("version").__version__)
        self.root.configure(bg=BG)
        _set_window_icon(self.root)

        settings = config.load_appearance()

        # Buttons first (packed to the bottom) so they stay visible however small the
        # window gets; everything else scrolls above them.
        self._build_buttons()
        body = self._make_scroll_area()

        tk.Label(
            body, text="Desktop Lyrics settings", fg=FG, bg=BG,
            font=("Segoe UI", 13, "bold"),
        ).pack(anchor="w", padx=16, pady=(16, 4))
        tk.Label(
            body, text="Changes are saved and applied to the strip as you make them.",
            fg=FG_DIM, bg=BG, font=("Segoe UI", 9),
        ).pack(anchor="w", padx=16, pady=(0, 12))

        colors = tk.Frame(body, bg=BG)
        colors.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(colors, text="Colors", fg=ACCENT, bg=BG, font=("Segoe UI", 10, "bold")).pack(
            anchor="w", pady=(0, 6)
        )
        self.unsung_row = ColorRow(colors, "Not-yet-sung", settings["karaoke_unsung_rgb"], self._changed)
        self.unsung_row.pack(fill="x", pady=2)
        self.sung_row = ColorRow(colors, "Already sung", settings["karaoke_sung_rgb"], self._changed)
        self.sung_row.pack(fill="x", pady=2)
        self.next_row = ColorRow(colors, "Next line", settings["next_line_rgb"], self._changed)
        self.next_row.pack(fill="x", pady=2)

        sizes = tk.Frame(body, bg=BG)
        sizes.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(sizes, text="Font sizes", fg=ACCENT, bg=BG, font=("Segoe UI", 10, "bold")).pack(
            anchor="w", pady=(0, 6)
        )
        self.curr_size_row = SizeRow(sizes, "Current line", settings["curr_font_size"], 10, 60, self._changed)
        self.curr_size_row.pack(fill="x", pady=2)
        self.next_size_row = SizeRow(sizes, "Next line", settings["next_font_size"], 6, 40, self._changed)
        self.next_size_row.pack(fill="x", pady=2)
        self.curr_min_row = SizeRow(
            sizes, "Current line (min)", settings["curr_font_min_size"], 6, 40, self._changed
        )
        self.curr_min_row.pack(fill="x", pady=2)
        self.next_min_row = SizeRow(
            sizes, "Next line (min)", settings["next_font_min_size"], 4, 30, self._changed
        )
        self.next_min_row.pack(fill="x", pady=2)

        dims = tk.Frame(body, bg=BG)
        dims.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(dims, text="Strip size", fg=ACCENT, bg=BG, font=("Segoe UI", 10, "bold")).pack(
            anchor="w", pady=(0, 6)
        )
        self.width_row = SizeRow(dims, "Width (px)", settings["desktop_width"], 300, 2000, self._changed)
        self.width_row.pack(fill="x", pady=2)
        self.height_row = SizeRow(dims, "Height (px)", settings["desktop_height"], 80, 500, self._changed)
        self.height_row.pack(fill="x", pady=2)
        self.offset_row = SizeRow(
            dims, "Lyric offset (ms)", settings["lyric_offset_ms"], -3000, 3000, self._changed
        )
        self.offset_row.pack(fill="x", pady=2)

        disp = tk.Frame(body, bg=BG)
        disp.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(disp, text="Display", fg=ACCENT, bg=BG, font=("Segoe UI", 10, "bold")).pack(
            anchor="w", pady=(0, 6)
        )
        self.title_var = tk.BooleanVar(value=bool(settings["show_title_card"]))
        for text, var in (("Show 'Artist - Song' title card first", self.title_var),):
            tk.Checkbutton(
                disp, text=text, variable=var, fg=FG, bg=BG, selectcolor=FIELD_BG,
                activebackground=BG, activeforeground=FG, font=("Segoe UI", 10), anchor="w",
                command=self._changed,
            ).pack(fill="x")
        self.outline_var = tk.DoubleVar(value=float(settings["outline_size"]))
        out_row = tk.Frame(disp, bg=BG)
        out_row.pack(fill="x", pady=2)
        tk.Label(out_row, text="Outline (px)", fg=FG_DIM, bg=BG, font=("Segoe UI", 10),
                 width=16, anchor="w").pack(side="left")
        self.outline_var.trace_add("write", lambda *_: self._changed())
        tk.Spinbox(out_row, from_=0, to=4, increment=0.5, textvariable=self.outline_var, width=6,
                   bg=FIELD_BG, fg=FG, buttonbackground=FIELD_BG, relief="flat",
                   font=("Consolas", 10)).pack(side="left")

        if winsys.is_windows():
            self.autostart_var = tk.BooleanVar(value=winsys.autostart_enabled())
            tk.Checkbutton(
                disp, text="Start with Windows", variable=self.autostart_var, fg=FG, bg=BG,
                selectcolor=FIELD_BG, activebackground=BG, activeforeground=FG,
                font=("Segoe UI", 10), anchor="w", command=self._autostart_changed,
            ).pack(fill="x", pady=(6, 0))
        else:
            self.autostart_var = None

        preview = tk.Frame(body, bg=BG)
        preview.pack(fill="x", padx=16, pady=(0, 12))
        tk.Label(preview, text="Preview", fg=ACCENT, bg=BG, font=("Segoe UI", 10, "bold")).pack(
            anchor="w", pady=(0, 6)
        )
        self.preview_canvas = tk.Canvas(
            preview, height=44, bg="#000000", highlightthickness=1,
            highlightbackground="#3a3a3a",
        )
        self.preview_canvas.pack(fill="x")
        self._draw_preview()
        for row in (self.unsung_row, self.sung_row, self.next_row):
            row.swatch.bind("<Button-1>", self._make_pick_and_refresh(row), add="+")

        self._built = True
        self._fit_window()

    # ------------------------------------------------------------ layout --

    def _build_buttons(self):
        btns = tk.Frame(self.root, bg=BG)
        btns.pack(fill="x", padx=16, pady=(4, 16), side="bottom")
        tk.Button(
            btns, text="Done", command=self._done, bg=ACCENT, fg="#0d0d0d",
            activebackground=ACCENT, font=("Segoe UI", 10, "bold"), relief="flat",
            padx=14, pady=6, cursor="hand2",
        ).pack(side="right")
        tk.Button(
            btns, text="Reset to defaults", command=self._reset, bg=FIELD_BG, fg=FG,
            activebackground=FIELD_BG, font=("Segoe UI", 10), relief="flat",
            padx=10, pady=6, cursor="hand2",
        ).pack(side="left")

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
        """Open at the content's natural size, capped to the screen."""
        try:
            self.root.update_idletasks()
            want_h = int(self._scroll_body.winfo_reqheight())
            want_w = int(self._scroll_body.winfo_reqwidth()) + 24
            max_h = int(self.root.winfo_screenheight() * 0.8) - 90    # room for the buttons + taskbar
            self._scroll_canvas.configure(width=want_w, height=max(200, min(want_h, max_h)))
        except Exception:
            log.warning("couldn't size the settings window from its content", exc_info=True)

    def _make_pick_and_refresh(self, row):
        def handler(_event=None):
            self._draw_preview()
        return handler

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
        for i, ch in enumerate(half):
            color = sung_hex if i < mid else unsung_hex
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
            "show_title_card": bool(self.title_var.get()),
            "outline_size": self._outline(),
        }

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
        if not winsys.set_autostart(bool(self.autostart_var.get())):
            self.autostart_var.set(winsys.autostart_enabled())
            messagebox.showerror("Start with Windows", "Couldn't change the setting. See logs/spoti-lyrics.log.")
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
        for row in (self.unsung_row, self.sung_row, self.next_row):
            row.swatch.configure(bg=row._hex())
            row.hex_label.configure(text=row._hex())
        self.curr_size_row.var.set(defaults["curr_font_size"])
        self.next_size_row.var.set(defaults["next_font_size"])
        self.curr_min_row.var.set(defaults["curr_font_min_size"])
        self.next_min_row.var.set(defaults["next_font_min_size"])
        self.width_row.var.set(defaults["desktop_width"])
        self.height_row.var.set(defaults["desktop_height"])
        self.offset_row.var.set(defaults["lyric_offset_ms"])
        self.title_var.set(defaults["show_title_card"])
        self.outline_var.set(defaults["outline_size"])
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
