"""Launcher: runs the lyrics strip as its own app with no console window.

Double-click this file (Windows runs .pyw with pythonw.exe), or use
LyricsOverlay.bat. Quit from the tray icon or the strip's right-click menu.
"""
import os
import sys

os.chdir(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.getcwd())

import app

app.main()
