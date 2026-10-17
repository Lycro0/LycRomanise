"""Small windows for the web view: its address with a QR code, and a window with just the QR code."""

import logging
import tkinter as tk

import mobile_server

log = logging.getLogger("lycro.mobile")

BG, FG, FG_DIM, ACCENT, FIELD_BG = "#0d0d0d", "#e8e8e8", "#9a9a9a", "#1db954", "#1a1a1a"


def _qr_photo(url, box=6):
    """A tk PhotoImage of the QR code for `url`, or None when the optional 'qrcode' package is missing."""
    try:
        import qrcode
    except Exception:
        return None
    try:
        qr = qrcode.QRCode(border=2, box_size=box)
        qr.add_data(url)
        qr.make(fit=True)
        matrix = qr.get_matrix()
        size = len(matrix)
        rows = []
        for row in matrix:
            line = "".join(("#000000 " if cell else "#ffffff ") * box for cell in row)
            rows.extend(["{" + line.strip() + "}"] * box)
        img = tk.PhotoImage(width=size * box, height=size * box)
        img.put(" ".join(rows))
        return img
    except Exception:
        log.warning("couldn't draw the QR code", exc_info=True)
        return None


_open = {}          # "info" / "qr" -> the window that is open, so a second click brings it forward


def _reuse(kind):
    """The already-open window of this kind, raised, or None."""
    win = _open.get(kind)
    if win is None:
        return None
    try:
        win.deiconify()
        win.lift()
        return win
    except tk.TclError:
        _open.pop(kind, None)
        return None


def show(parent, port, enabled=True):
    """Open the 'Web view' window. `parent` is any Tk widget."""
    existing = _reuse("info")
    if existing is not None:
        return existing
    url = mobile_server.mobile_url(port)
    win = tk.Toplevel(parent)
    _open["info"] = win
    win.title("LycRomanise - Web view")
    win.configure(bg=BG)
    win.attributes("-topmost", True)
    tk.Label(win, text="Lyrics on the web (phone, tablet, OBS)", fg=FG, bg=BG,
             font=("Segoe UI", 12, "bold")).pack(padx=20, pady=(16, 4))
    steps = ("1. Put the phone on the same Wi-Fi as this PC.\n"
             "2. Open this address in Safari / Chrome (or scan the code).\n"
             "3. Tap the page once for full screen.")
    tk.Label(win, text=steps, fg=FG_DIM, bg=BG, font=("Segoe UI", 10), justify="left").pack(padx=20, pady=(0, 8))
    if not enabled:
        tk.Label(win, text="Web view is currently OFF - turn it on in Settings or the tray menu.",
                 fg="#ff8a8a", bg=BG, font=("Segoe UI", 10, "bold"), wraplength=320).pack(padx=20, pady=(0, 8))
    field = tk.Entry(win, font=("Consolas", 12), justify="center", bg=FIELD_BG, fg=FG, relief="flat",
                     insertbackground=FG, readonlybackground=FIELD_BG)
    field.insert(0, url)
    field.configure(state="readonly")
    field.pack(fill="x", padx=20, ipady=6)
    tk.Label(win, text="OBS / streaming: use this same address as the Browser source (it is transparent automatically).",
             fg=FG_DIM, bg=BG, font=("Segoe UI", 9), wraplength=320).pack(padx=20, pady=(8, 2))
    photo = _qr_photo(url)
    if photo is not None:
        lbl = tk.Label(win, image=photo, bg="#ffffff", bd=6, relief="flat")
        lbl.image = photo
        lbl.pack(pady=12)
    else:
        tk.Label(win, text="(Install the optional 'qrcode' package to get a scannable code here.)",
                 fg=FG_DIM, bg=BG, font=("Segoe UI", 9), wraplength=320).pack(pady=8)

    def copy():
        win.clipboard_clear()
        win.clipboard_append(url)

    row = tk.Frame(win, bg=BG)
    row.pack(pady=(0, 16))
    tk.Button(row, text="Copy address", command=copy, bg=FIELD_BG, fg=FG, relief="flat", padx=12, pady=5,
              font=("Segoe UI", 10)).pack(side="left", padx=4)
    tk.Button(row, text="Close", command=win.destroy, bg=ACCENT, fg="#0d0d0d", relief="flat", padx=14, pady=5,
              font=("Segoe UI", 10, "bold")).pack(side="left", padx=4)
    return win


def show_qr(parent, port):
    """A small window with nothing but the QR code for the web view address."""
    existing = _reuse("qr")
    if existing is not None:
        return existing
    url = mobile_server.mobile_url(port)
    win = tk.Toplevel(parent)
    _open["qr"] = win
    win.title("LycRomanise - QR code")
    win.configure(bg="#ffffff")
    win.attributes("-topmost", True)
    win.resizable(False, False)
    photo = _qr_photo(url, box=7)
    if photo is not None:
        lbl = tk.Label(win, image=photo, bg="#ffffff", bd=0)
        lbl.image = photo
        lbl.pack(padx=10, pady=10)
    else:
        tk.Label(win, text="The QR code needs the 'qrcode' package.\nUse the address instead:\n" + url,
                 bg="#ffffff", fg="#222222", font=("Segoe UI", 10), padx=20, pady=20).pack()
    win.bind("<Escape>", lambda _e: win.destroy())
    return win
