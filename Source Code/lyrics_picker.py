"""The "Wrong lyrics? Pick another" window: lists other lyric sets the lyrics site has for the playing
song so you can switch to the right one. Pure UI: app.py supplies the search and what a pick does."""

import tkinter as tk

BG = "#1e1b3a"
FG = "#f2f0ff"
MUTED = "#a9a4d0"


def describe(alt):
    """One list row: 'Title - Artist   3:45   54 lines   (Album)'."""
    secs = int(round(alt.get("dur_s") or 0))
    parts = ["%s - %s" % (alt.get("title") or "?", alt.get("artist") or "?"),
             "%d:%02d" % (secs // 60, secs % 60), "%d lines" % (alt.get("lines") or 0)]
    if alt.get("album"):
        parts.append("(%s)" % alt["album"])
    return "   ".join(parts)


class LyricsPicker:
    def __init__(self, win, song_text, on_pick, on_close):
        self.win, self._on_pick, self._on_close = win, on_pick, on_close
        self._alts = []
        win.title("Wrong lyrics?")
        win.configure(bg=BG)
        win.minsize(560, 300)
        win.protocol("WM_DELETE_WINDOW", self.close)

        body = tk.Frame(win, bg=BG, padx=18, pady=14)
        body.pack(fill="both", expand=True)
        tk.Label(body, text="Pick the lyrics that match", bg=BG, fg=FG,
                 font=("Segoe UI", 13, "bold")).pack(anchor="w")
        tk.Label(body, text=song_text, bg=BG, fg=MUTED, font=("Segoe UI", 10),
                 anchor="w", justify="left", wraplength=520).pack(anchor="w", pady=(2, 8), fill="x")

        frame = tk.Frame(body, bg=BG)
        frame.pack(fill="both", expand=True)
        self.listbox = tk.Listbox(frame, activestyle="none", bg="#2a2650", fg=FG, relief="flat",
                                  selectbackground="#7c6cf0", selectforeground="white",
                                  font=("Segoe UI", 10), height=10, exportselection=False)
        scroll = tk.Scrollbar(frame, command=self.listbox.yview)
        self.listbox.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.listbox.pack(side="left", fill="both", expand=True)
        self.listbox.bind("<Double-Button-1>", lambda _e: self._use())
        self.listbox.bind("<Return>", lambda _e: self._use())

        self.status = tk.Label(body, text="Searching...", bg=BG, fg=MUTED, font=("Segoe UI", 9), anchor="w")
        self.status.pack(fill="x", pady=(8, 0))
        buttons = tk.Frame(body, bg=BG)
        buttons.pack(fill="x", pady=(10, 0))
        self.use_button = self._button(buttons, "Use these lyrics", self._use, primary=True)
        self.use_button.configure(state="disabled")
        self._button(buttons, "Close", self.close)

    @staticmethod
    def _button(parent, text, command, primary=False):
        b = tk.Button(parent, text=text, command=command, relief="flat", padx=14, pady=5,
                      bg="#7c6cf0" if primary else "#3a3566", fg="white", activebackground="#9486ff",
                      activeforeground="white", font=("Segoe UI", 10, "bold" if primary else "normal"))
        b.pack(side="left", padx=(0, 8))
        return b

    def set_results(self, alts, message=None):
        """Fill the list (call on the Tk thread). `message` replaces the status line."""
        self._alts = list(alts)
        self.listbox.delete(0, "end")
        for alt in self._alts:
            self.listbox.insert("end", describe(alt))
        if self._alts:
            self.listbox.selection_set(0)
            self.use_button.configure(state="normal")
        self.status.configure(text=message or (
            "%d lyric sets found, closest in length first. Double-click to use one." % len(self._alts)
            if self._alts else "No other lyrics found."))

    def _use(self):
        sel = self.listbox.curselection()
        if not sel:
            return
        self.status.configure(text="Applying...")
        try:
            self._on_pick(self._alts[sel[0]])
        finally:
            self.close()

    def close(self):
        try:
            self.win.destroy()
        except tk.TclError:
            pass
        self._on_close()
