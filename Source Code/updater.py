"""Checks GitHub Releases for a newer version and can install it.

check() only looks. If the user says yes, download() fetches the release's
LycRomanise_Setup.exe (installed copies) or LycRomanise.exe (portable copies) and apply()
installs it and restarts the app. Only the .exe build can update itself; a copy run from
source just opens the release page.
"""

import hashlib
import logging
import os
import re
import subprocess
import sys
import urllib.parse

import requests

from paths import APP_DIR, DATA_DIR, FROZEN
from version import __version__, GITHUB_REPO, RELEASES_URL

log = logging.getLogger("spoti.update")
API_URL = "https://api.github.com/repos/%s/releases/latest" % GITHUB_REPO

EXE_NAME = "LycRomanise.exe"
SETUP_NAME = "LycRomanise_Setup.exe"
UPDATE_DIR = os.path.join(DATA_DIR, "update")
SKIP_PATH = os.path.join(DATA_DIR, "skipped_update.txt")
_ALLOWED_HOSTS = ("github.com", "githubusercontent.com")


def _parse(v):
    """'v1.2.3-beta' -> (1, 2, 3). Unparseable -> ()."""
    nums = re.findall(r"\d+", (v or "").split("-")[0])
    return tuple(int(n) for n in nums[:4])


def is_newer(latest, current=__version__):
    a, b = _parse(latest), _parse(current)
    if not a or not b:
        return False
    n = max(len(a), len(b))
    return a + (0,) * (n - len(a)) > b + (0,) * (n - len(b))


def check():
    """Returns {"tag", "url", "notes", "assets": {name: {"url", "size", "sha256"}}} if a newer
    release exists, else None. Errors (offline, rate limited, no releases yet) return None quietly."""
    try:
        r = requests.get(API_URL, timeout=(5, 10),
                         headers={"Accept": "application/vnd.github+json",
                                  "User-Agent": "LycRomanise/%s" % __version__})
        if r.status_code != 200:
            log.info("update check: HTTP %s", r.status_code)
            return None
        data = r.json()
        if data.get("draft") or data.get("prerelease"):
            return None
        tag = data.get("tag_name") or data.get("name") or ""
        if not is_newer(tag):
            log.info("up to date (%s, latest release %s)", __version__, tag)
            return None
        assets = {}
        for a in data.get("assets") or []:
            digest = str(a.get("digest") or "")
            assets[a.get("name") or ""] = {
                "url": a.get("browser_download_url") or "",
                "size": int(a.get("size") or 0),
                "sha256": digest.split(":", 1)[1].lower() if digest.lower().startswith("sha256:") else "",
            }
        log.info("update available: %s (running %s), files: %s", tag, __version__, ", ".join(assets) or "none")
        return {"tag": tag, "url": data.get("html_url") or RELEASES_URL,
                "notes": (data.get("body") or "").strip(), "assets": assets}
    except Exception as exc:
        log.info("update check failed: %s", exc)
    return None


# ------------------------------------------------------------ install paths --

def install_kind():
    """'installer' when this copy was put in place by LycRomanise_Setup.exe (Inno Setup leaves
    unins000.exe beside it), else 'portable'."""
    return "installer" if os.path.isfile(os.path.join(APP_DIR, "unins000.exe")) else "portable"


def _asset_for_this_copy(release):
    name = SETUP_NAME if install_kind() == "installer" else EXE_NAME
    return name, (release.get("assets") or {}).get(name)


def can_self_update(release):
    """True when this is the Windows .exe, the release has the matching file, and (portable
    copies) the folder it sits in is writable."""
    if not (FROZEN and sys.platform == "win32"):
        return False
    _name, asset = _asset_for_this_copy(release)
    if not asset or not _trusted_url(asset["url"]):
        return False
    return install_kind() == "installer" or os.access(APP_DIR, os.W_OK)


def _trusted_url(url):
    p = urllib.parse.urlparse(url or "")
    host = (p.hostname or "").lower()
    return p.scheme == "https" and any(host == h or host.endswith("." + h) for h in _ALLOWED_HOSTS)


# ---------------------------------------------------------------- download --

def download(release, progress=None):
    """Download the right file for this copy into the update folder and verify it. progress(done,
    total) is called as bytes arrive. Returns (path, kind); raises RuntimeError with a message
    fit for the user."""
    name, asset = _asset_for_this_copy(release)
    if not asset or not _trusted_url(asset["url"]):
        raise RuntimeError("This release has no %s to download." % name)
    os.makedirs(UPDATE_DIR, exist_ok=True)
    dest = os.path.join(UPDATE_DIR, "%s.%s" % (release["tag"].lstrip("vV"), name))
    part = dest + ".part"
    sha = hashlib.sha256()
    done = 0
    try:
        with requests.get(asset["url"], stream=True, timeout=(8, 30),
                          headers={"User-Agent": "LycRomanise/%s" % __version__}) as r:
            if r.status_code != 200:
                raise RuntimeError("Download failed (HTTP %s)." % r.status_code)
            if not _trusted_url(r.url):
                raise RuntimeError("Download was redirected to an unexpected address.")
            total = int(r.headers.get("Content-Length") or asset["size"] or 0)
            with open(part, "wb") as f:
                for chunk in r.iter_content(chunk_size=64 * 1024):
                    if chunk:
                        f.write(chunk)
                        sha.update(chunk)
                        done += len(chunk)
                        if progress:
                            progress(done, total)
        if asset["size"] and done != asset["size"]:
            raise RuntimeError("Download was incomplete.")
        if asset["sha256"] and sha.hexdigest() != asset["sha256"]:
            raise RuntimeError("Downloaded file didn't match the published checksum.")
        with open(part, "rb") as f:
            if f.read(2) != b"MZ":
                raise RuntimeError("Downloaded file isn't a Windows program.")
        os.replace(part, dest)
    except RuntimeError:
        _remove(part)
        raise
    except (requests.RequestException, OSError) as exc:
        _remove(part)
        raise RuntimeError("Download failed: %s" % exc)
    log.info("downloaded %s (%d bytes)", dest, done)
    return dest, install_kind()


def _remove(path):
    try:
        os.remove(path)
    except OSError:
        pass


# ------------------------------------------------------------------- apply --

_DETACHED = 0x00000008          # DETACHED_PROCESS
_NEW_GROUP = 0x00000200         # CREATE_NEW_PROCESS_GROUP
_NO_WINDOW = 0x08000000         # CREATE_NO_WINDOW


def _clean_env():
    """A PyInstaller one-file exe passes its unpack folder to child processes; a child that is
    itself the new LycRomanise.exe must start fresh instead of reusing the folder that is about to
    be deleted."""
    env = dict(os.environ)
    env["PYINSTALLER_RESET_ENVIRONMENT"] = "1"
    env.pop("_MEIPASS2", None)
    return env


def apply(path, kind):
    """Start the install and return True; the caller must then quit the app straight away.
    installer: the Inno Setup installer runs with a progress bar only, then relaunches the app.
    portable: a tiny script waits for this exe to close, swaps the file and starts it again."""
    try:
        if kind == "installer":
            args = [path, "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1"]
            subprocess.Popen(args, close_fds=True, env=_clean_env(), creationflags=_DETACHED | _NEW_GROUP)
        else:
            target = os.path.abspath(sys.executable)
            script = os.path.join(UPDATE_DIR, "apply_update.cmd")
            with open(script, "w", encoding="utf-8", newline="\r\n") as f:
                f.write(_swap_script(path, target))
            subprocess.Popen(["cmd.exe", "/c", script], close_fds=True, env=_clean_env(),
                             creationflags=_NO_WINDOW | _NEW_GROUP)
        log.info("update started (%s): %s", kind, path)
        return True
    except Exception:
        log.exception("couldn't start the update")
        return False


def _swap_script(new_path, target):
    return "\n".join([
        "@echo off",
        "chcp 65001 >nul",
        'set "NEW=%s"' % new_path,
        'set "OLD=%s"' % target,
        "set /a tries=0",
        ":retry",
        'move /y "%NEW%" "%OLD%" >nul 2>&1',
        "if not errorlevel 1 goto done",
        "set /a tries+=1",
        "if %tries% geq 90 exit /b 1",
        "ping -n 2 127.0.0.1 >nul",
        "goto retry",
        ":done",
        'start "" "%OLD%"',
        '(goto) 2>nul & del "%~f0"',
        "",
    ])


# ------------------------------------------------------------ skipped tags --

def skipped_tag():
    try:
        with open(SKIP_PATH, "r", encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def skip_version(tag):
    try:
        with open(SKIP_PATH, "w", encoding="utf-8") as f:
            f.write(tag or "")
    except OSError:
        pass
