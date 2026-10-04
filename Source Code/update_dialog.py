"""The "new version available" window: Update now / Later / Skip this version, with a progress bar
while the download runs. Pure UI: app.py supplies what happens on each button."""

import tkinter as tk
from tkinter import ttk

BG = "#1e1b3a"
FG = "#f2f0ff"
MUTED = "#a9a4d0"


class UpdateDialog:
    def __init__(self, win, tag, current, notes, on_update, on_later, on_skip, on_open_page):
        self.win = win
        self._on_open_page = on_open_page
        win.title("LycRomanise update")
        win.configure(bg=BG)
        win.resizable(False, False)

        body = tk.Frame(win, bg=BG, padx=22, pady=18)
        body.pack(fill="both", expand=True)
        tk.Label(body, text="A new version is available", bg=BG, fg=FG,
                 font=("Segoe UI", 13, "bold")).pack(anchor="w")
        tk.Label(body, text="LycRomanise %s is out (you have v%s)." % (tag, current), bg=BG, fg=MUTED,
                 font=("Segoe UI", 10)).pack(anchor="w", pady=(2, 10))

        notes = _short(notes)
        if notes:
            box = tk.Text(body, width=58, height=min(8, notes.count("\n") + 2), bg="#2a2650", fg=FG,
                          relief="flat", wrap="word", font=("Segoe UI", 9), padx=8, pady=6)
            box.insert("1.0", notes)
            box.configure(state="disabled")
            box.pack(fill="x")

        self.status = tk.Label(body, text="", bg=BG, fg=MUTED, font=("Segoe UI", 9), anchor="w")
        self.status.pack(fill="x", pady=(10, 0))
        self.bar = ttk.Progressbar(body, length=400, mode="determinate", maximum=100)

        self.buttons = tk.Frame(body, bg=BG)
        self.buttons.pack(fill="x", pady=(14, 0))
        self._buttons = []
        self._add("Update now", lambda: self._start(on_update), primary=True)
        self._add("Later", on_later)
        self._add("Skip this version", on_skip)

    def _add(self, text, command, primary=False):
        b = tk.Button(self.buttons, text=text, command=command, relief="flat", padx=14, pady=5,
                      bg="#7c6cf0" if primary else "#3a3566", fg="white", activebackground="#9486ff",
                      activeforeground="white", font=("Segoe UI", 10, "bold" if primary else "normal"),
                      cursor="hand2")
        b.pack(side="left", padx=(0, 8))
        self._buttons.append(b)

    def _start(self, on_update):
        for b in self._buttons:
            b.configure(state="disabled")
        self.status.configure(text="Downloading...")
        self.bar.pack(fill="x", pady=(6, 0), before=self.buttons)
        on_update()

    # --- called from the Tk thread by app.py ---------------------------------

    def progress(self, done, total):
        if total:
            self.bar.configure(mode="determinate", value=min(100.0, done * 100.0 / total))
            self.status.configure(text="Downloading... %.1f of %.1f MB" % (done / 1048576, total / 1048576))
        else:
            self.status.configure(text="Downloading... %.1f MB" % (done / 1048576))

    def installing(self):
        self.bar.configure(value=100)
        self.status.configure(text="Installing - LycRomanise will restart in a moment...")

    def failed(self, message):
        """Download/install problem: say why and offer the release page instead."""
        self.status.configure(text=message, fg="#ff9d9d", wraplength=400, justify="left")
        self.bar.pack_forget()
        for b in self._buttons:
            b.destroy()
        self._buttons = []
        self._add("Open download page", self._on_open_page, primary=True)
        self._add("Close", self.win.destroy)


def _short(notes):
    """First lines of the release description, without markdown clutter."""
    lines = []
    for raw in (notes or "").splitlines():
        line = raw.strip().lstrip("#").strip().replace("**", "").replace("`", "")
        if line:
            lines.append(line)
        if len(lines) >= 7:
            break
    text = "\n".join(lines)
    return text[:600] + ("..." if len(text) > 600 else "")
