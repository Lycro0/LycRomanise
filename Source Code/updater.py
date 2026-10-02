"""Checks GitHub Releases for a newer version. Never downloads or installs anything:
it only tells the user and opens the release page."""

import logging
import re

import requests

from version import __version__, GITHUB_REPO, RELEASES_URL

log = logging.getLogger("spoti.update")
API_URL = "https://api.github.com/repos/%s/releases/latest" % GITHUB_REPO


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
    """Returns (latest_tag, page_url) if a newer release exists, else None.
    Errors (offline, rate limited, no releases yet) return None quietly."""
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
        if is_newer(tag):
            log.info("update available: %s (running %s)", tag, __version__)
            return tag, data.get("html_url") or RELEASES_URL
        log.info("up to date (%s, latest release %s)", __version__, tag)
    except Exception as exc:
        log.info("update check failed: %s", exc)
    return None
