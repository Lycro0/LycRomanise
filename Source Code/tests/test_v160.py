"""Regression tests for the v1.6.0 review (security hardening and bug fixes).
Run with:  python -m unittest discover tests   (from the source folder). Pure logic plus a local test server; no windows."""
import http.client
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
os.environ.setdefault("LOCALAPPDATA", tempfile.mkdtemp())

import config
import lyrics_provider as lp
import mobile_server as ms
import paths
import updater
import version


class AtomicWriteTests(unittest.TestCase):
    def test_parallel_writers_do_not_lose_writes_or_leave_temp_files(self):
        d = tempfile.mkdtemp()
        target = os.path.join(d, "x.json")
        errors = []

        def work(n):
            try:
                for i in range(40):
                    paths.write_json_atomic(target, {"n": n, "i": i})
            except Exception as exc:          # the old shared ".tmp" name made os.replace fail here
                errors.append(exc)
        threads = [threading.Thread(target=work, args=(n,)) for n in range(6)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(errors, [])
        with open(target, encoding="utf-8") as f:
            self.assertIn("n", json.load(f))
        self.assertEqual(os.listdir(d), ["x.json"])

    def test_failed_write_removes_its_temp_file(self):
        d = tempfile.mkdtemp()
        with self.assertRaises(TypeError):
            paths.write_json_atomic(os.path.join(d, "x.json"), {"bad": object()})
        self.assertEqual(os.listdir(d), [])


class ConfigTests(unittest.TestCase):
    def test_nan_and_infinity_fall_back_to_defaults(self):
        out = config.sanitize({"lyric_offset_ms": float("nan"), "desktop_width": float("inf"),
                               "spawn_y_percent": float("-inf")})
        self.assertEqual(out["lyric_offset_ms"], config.DEFAULT_APPEARANCE["lyric_offset_ms"])
        self.assertEqual(out["desktop_width"], config.DEFAULT_APPEARANCE["desktop_width"])
        self.assertEqual(out["spawn_y_percent"], config.DEFAULT_APPEARANCE["spawn_y_percent"])

    def test_ordinary_values_still_clamp(self):
        out = config.sanitize({"desktop_height": 9999, "mobile_port": "80", "lyric_offset_ms": "250"})
        self.assertEqual((out["desktop_height"], out["mobile_port"], out["lyric_offset_ms"]), (1500, 1024, 250.0))


class LrcHardeningTests(unittest.TestCase):
    def test_line_length_and_control_characters(self):
        lines = lp.parse_lrc("[00:01.00]" + "A" * 5000 + "\n[00:02.00]bad\x07\x1bchars\n[00:03.00]ok")
        self.assertEqual(len(lines[0][1]), lp.MAX_LINE_CHARS)
        self.assertEqual(lines[1][1], "bad chars")
        self.assertEqual(lines[2][1], "ok")

    def test_line_count_is_capped(self):
        text = "\n".join("[%02d:%02d.00]line %d" % (i // 60, i % 60, i) for i in range(9000))
        self.assertLessEqual(len(lp.parse_lrc(text)), lp.MAX_LRC_LINES)

    def test_cache_entry_validation(self):
        self.assertEqual(lp._valid_lines([[1, "a"], (2.5, "b")]), [(1.0, "a"), (2.5, "b")])
        for bad in ("text", [[1]], [[1, "a", 3]], [["x", "a"]], [[1, 5]], [[True, "a"]], [[float("nan"), "a"]], None):
            self.assertIsNone(lp._valid_lines(bad), bad)

    def test_damaged_cache_entry_is_dropped_not_fatal(self):
        d = tempfile.mkdtemp()
        key = lp._cache_key("Song", "Artist", 200000)
        with mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None):
            lp._ensure_loaded()
            lp._cache[key] = {"v": lp.CACHE_VERSION, "synced": [[1, "ok"], 7], "plain": None}
            self.assertIsNone(lp._cache_lookup(key))
            self.assertNotIn(key, lp._cache)

    def test_odd_json_types_from_the_lyrics_site(self):
        self.assertEqual((lp._ms("x"), lp._ms(None), lp._ms(True), lp._ms(-3), lp._ms(212.5)), (0, 0, 0, 0, 212500))
        resp = mock.Mock()
        resp.json.return_value = [{"id": 1, "trackName": 5, "artistName": None, "duration": "long",
                                   "syncedLyrics": "[00:01.00]x"}, "junk", {"syncedLyrics": 3}]
        with mock.patch.object(lp, "_http", return_value=resp):
            found = lp._lrclib_search({"q": "x"})
        self.assertEqual([(c["name"], c["artists"], c["dur_ms"]) for c in found], [("", [""], 0)])

    def test_failed_cache_write_is_retried(self):
        d = tempfile.mkdtemp()
        with mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None):
            lp._ensure_loaded()
            lp._schedule_flush()
            lp._flush_timer.cancel()
            with mock.patch.object(lp, "write_json_atomic", side_effect=OSError("disk full")):
                lp.flush_cache()
            self.assertTrue(lp._dirty)
            lp.flush_cache()
            self.assertFalse(lp._dirty)
            self.assertTrue(os.path.exists(os.path.join(d, "c.json")))


class _Replies(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_GET(self):
        try:
            if self.path == "/big":
                body = b"x" * (lp.MAX_BODY_BYTES + 1000)
            else:
                body = json.dumps({"syncedLyrics": "[00:01.00]hi"}).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except OSError:
            pass


class HttpLimitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), _Replies)
        cls.srv.handle_error = lambda *a: None
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.base = "http://127.0.0.1:%d" % cls.srv.server_address[1]

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()

    def test_normal_answer_is_readable(self):
        with mock.patch.object(lp, "_record_result"):
            resp = lp._http("t", "GET", self.base + "/ok")
        self.assertEqual(resp.json()["syncedLyrics"], "[00:01.00]hi")

    def test_oversized_answer_is_refused(self):
        with mock.patch.object(lp, "_record_result") as rec:
            self.assertIsNone(lp._http("t", "GET", self.base + "/big"))
        rec.assert_called_with("t", False)


class MobileServerTests(unittest.TestCase):
    def test_host_allow_list(self):
        ok = ["192.168.1.5:8765", "[::1]:8765", "localhost:8765", "MYPC", "mypc.local", "mypc.lan"]
        bad = ["evil.example.com", "mypc.evil.com", "mypc.local.evil.com", "mypcevil.com", "", None]
        for h in ok:
            self.assertTrue(ms.host_allowed(h, "mypc"), h)
        for h in bad:
            self.assertFalse(ms.host_allowed(h, "mypc"), h)
        self.assertTrue(ms.host_allowed("lyrics.home.example", "mypc", extra=("lyrics.home.example",)))

    def test_server_headers_and_rebinding_refusal(self):
        server = ms.MobileServer("Lyc<b>")
        self.assertTrue(server.start(0))
        try:
            port = server._httpd.server_address[1]
            server.publish({"kind": "lyric", "top": "hi", "fraction": float("nan"), "rate": float("inf"), "playing": True})

            def get(path, host=None, method="GET"):
                c = http.client.HTTPConnection("127.0.0.1", port, timeout=3)
                c.putrequest(method, path, skip_host=True)
                c.putheader("Host", host or "127.0.0.1:%d" % port)
                c.endheaders()
                r = c.getresponse()
                return r.status, dict(r.getheaders()), r.read()
            status, headers, body = get("/")
            self.assertEqual(status, 200)
            self.assertIn("default-src 'none'", headers["Content-Security-Policy"])
            self.assertEqual(headers["X-Content-Type-Options"], "nosniff")
            self.assertIn(b"Lyc&lt;b&gt;", body)
            status, _h, body = get("/state")
            self.assertEqual(json.loads(body)["fraction"], 0.0)          # valid JSON even for NaN
            self.assertEqual(get("/state", host="evil.example.com")[0], 403)
            self.assertEqual(get("/", host="evil.example.com", method="HEAD")[0], 403)
            self.assertEqual(get("/", method="HEAD")[0], 200)
        finally:
            server.stop()


class UpdaterTests(unittest.TestCase):
    def release(self, digest="a" * 64):
        return {"tag": "v9.9.9", "url": "x", "notes": "",
                "assets": {updater.EXE_NAME: {"url": "https://github.com/o/r/releases/download/v9/LycRomanise.exe",
                                              "size": 10, "sha256": digest}}}

    def test_self_update_needs_a_published_checksum(self):
        with mock.patch.object(updater, "FROZEN", True), mock.patch.object(updater.sys, "platform", "win32"), \
                mock.patch.object(updater, "install_kind", return_value="portable"), \
                mock.patch.object(updater.os, "access", return_value=True):
            self.assertTrue(updater.can_self_update(self.release()))
            self.assertFalse(updater.can_self_update(self.release(digest="")))
            self.assertFalse(updater.can_self_update(self.release(digest="not-a-hash")))

    def test_download_refuses_without_checksum(self):
        with mock.patch.object(updater, "install_kind", return_value="portable"):
            with self.assertRaises(RuntimeError):
                updater.download(self.release(digest=""))

    def test_file_changed_after_verification_is_not_installed(self):
        d = tempfile.mkdtemp()
        path = os.path.join(d, "setup.exe")
        with open(path, "wb") as f:
            f.write(b"MZgood")
        import hashlib
        updater._verified[os.path.normcase(path)] = hashlib.sha256(b"MZgood").hexdigest()
        self.assertTrue(updater._still_verified(path))
        with open(path, "wb") as f:
            f.write(b"MZevil")
        self.assertFalse(updater._still_verified(path))
        with mock.patch.object(updater.subprocess, "Popen") as popen:
            self.assertFalse(updater.apply(path, "portable"))
            popen.assert_not_called()
        self.assertFalse(updater._still_verified(os.path.join(d, "never-downloaded.exe")))

    def test_release_page_link_must_be_github(self):
        data = {"tag_name": "v99.0.0", "html_url": "https://evil.example/x", "assets": [], "body": "n"}
        resp = mock.Mock(status_code=200)
        resp.json.return_value = data
        with mock.patch.object(updater.requests, "get", return_value=resp):
            self.assertEqual(updater.check()["url"], version.RELEASES_URL)
        data["html_url"] = "https://github.com/Lycro0/LycRomanise/releases/tag/v99.0.0"
        with mock.patch.object(updater.requests, "get", return_value=resp):
            self.assertEqual(updater.check()["url"], data["html_url"])


class VersionTests(unittest.TestCase):
    def test_version_is_current_everywhere(self):
        self.assertEqual(version.__version__, "1.7.0")
        here = os.path.join(os.path.dirname(__file__), "..", "..", "builder")
        for name, needle in (("installer.iss", '"1.7.0"'), ("version_info.txt", "1.7.0.0")):
            path = os.path.join(here, name)
            if os.path.exists(path):          # these live in the "builder" folder
                with open(path, encoding="utf-8") as f:
                    self.assertIn(needle, f.read(), name)


if __name__ == "__main__":
    unittest.main()
