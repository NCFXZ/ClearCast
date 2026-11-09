"""Decode synthetic HTTP/HLS media and exercise the production source policy.

Runs only loopback HTTP(S) servers; no AirPlay receiver ports or real devices.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import collections
import contextlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import ssl
import subprocess
import tempfile
import threading

ROOT = Path(__file__).resolve().parents[2]


def run(command, **options):
    result = subprocess.run([str(value) for value in command], capture_output=True, text=True, timeout=35, **options)
    if result.returncode:
        raise AssertionError(f"Fixture command failed ({result.returncode}): {result.stderr[-1500:]} {result.stdout[-1500:]}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--runtime", type=Path, default=ROOT / ".runtime/receiver")
    parser.add_argument("--ffmpeg", type=Path, default=ROOT / ".runtime/msys64/ucrt64/bin/ffmpeg.exe")
    parser.add_argument("--openssl", type=Path, default=ROOT / ".runtime/msys64/ucrt64/bin/openssl.exe")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--case", help="Run only case names containing this text")
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    environment = dict(os.environ)
    environment["PATH"] = str(runtime) + os.pathsep + str(runtime / "bin") + os.pathsep + environment.get("PATH", "")
    environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = str(runtime / "lib/gstreamer-1.0")
    environment["GST_PLUGIN_PATH_1_0"] = ""
    scanner = runtime / "gst-plugin-scanner.exe"
    if not scanner.is_file():
        scanner = runtime / "libexec/gstreamer-1.0/gst-plugin-scanner.exe"
    environment["GST_PLUGIN_SCANNER_1_0"] = str(scanner)
    environment["GIO_EXTRA_MODULES"] = str(runtime / "lib/gio/modules")
    checks = []
    with tempfile.TemporaryDirectory(prefix="direct-http-", dir=ROOT / ".runtime") as temporary:
        work = Path(temporary)
        environment["GST_REGISTRY"] = str(work / "registry.bin")
        tools_environment = dict(os.environ)
        tools_environment["PATH"] = str(args.ffmpeg.parent.resolve()) + os.pathsep + tools_environment.get("PATH", "")
        run([args.ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-t", "2",
             "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15", "-movflags", "+faststart", work / "video.mp4"], env=tools_environment)
        run([args.ffmpeg, "-v", "error", "-i", work / "video.mp4", "-c", "copy", "-hls_time", "1", "-hls_list_size", "0",
             "-hls_segment_filename", work / "segment-%03d.ts", work / "stream.m3u8"], env=tools_environment)
        run([args.openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=127.0.0.1",
             "-addext", "subjectAltName=IP:127.0.0.1", "-addext", "basicConstraints=critical,CA:TRUE",
             "-keyout", work / "key.pem", "-out", work / "certificate.pem"], env=tools_environment)
        requests = collections.Counter()
        lock = threading.Lock()

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                path = self.path.split("?", 1)[0]
                with lock:
                    requests[path] += 1
                if path.startswith("/redirect"):
                    self.send_response(302)
                    self.send_header("Location", "/segment-000.ts" if path.endswith(".ts") else "/video.mp4")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                if path.startswith("/blocked-"):
                    authority = f"127.0.0.1:{self.server.server_port}"
                    location = {
                        "/blocked-credentials.m3u8": f"http://user:secret@{authority}/segment-000.ts",
                        "/blocked-file.m3u8": (work / "video.mp4").as_uri(),
                        "/blocked-appsrc.m3u8": "appsrc://buffer",
                        "/blocked-redirect.m3u8": f"http://{authority}/redirect.ts",
                    }[path]
                    data = ("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n"
                            f"#EXTINF:2,\n{location}\n#EXT-X-ENDLIST\n").encode()
                    content_type = "application/vnd.apple.mpegurl"
                else:
                    filename = work / path.lstrip("/")
                    if filename.parent != work or not filename.is_file():
                        self.send_error(404)
                        return
                    data = filename.read_bytes()
                    content_type = "application/vnd.apple.mpegurl" if filename.suffix == ".m3u8" else "video/mp2t" if filename.suffix == ".ts" else "video/mp4"
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                with contextlib.suppress(BrokenPipeError, ConnectionResetError):
                    self.wfile.write(data)

        http = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        https = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(work / "certificate.pem", work / "key.pem")
        https.socket = tls.wrap_socket(https.socket, server_side=True)
        workers = [threading.Thread(target=server.serve_forever, daemon=True) for server in (http, https)]
        for worker in workers:
            worker.start()
        try:
            base = f"http://127.0.0.1:{http.server_port}"
            secure = f"https://127.0.0.1:{https.server_port}"
            cases = [("HTTP MP4 decoded", base + "/video.mp4", False, None),
                     ("HTTP HLS decoded", base + "/stream.m3u8", False, None),
                     ("HTTP redirect rejected", base + "/redirect.mp4", True, "/video.mp4"),
                     ("Nested HLS redirect rejected", base + "/blocked-redirect.m3u8", True, "/segment-000.ts"),
                     ("Nested HLS credentials rejected", base + "/blocked-credentials.m3u8", True, "/segment-000.ts"),
                     ("Nested local-file URI rejected", base + "/blocked-file.m3u8", True, None),
                     ("Nested appsrc URI rejected", base + "/blocked-appsrc.m3u8", True, None),
                     ("Untrusted HTTPS certificate rejected", secure + "/video.mp4", True, None),
                     ("Trusted HTTPS MP4 decoded", secure + "/video.mp4", False, None),
                     ("Trusted HTTPS HLS decoded", secure + "/stream.m3u8", False, None)]
            for name, uri, rejection, forbidden_fetch in cases:
                if args.case and args.case.casefold() not in name.casefold():
                    continue
                before = requests[forbidden_fetch] if forbidden_fetch else 0
                certificate = runtime / "certs/ca-bundle.crt"
                if not certificate.is_file():
                    certificate = runtime / "etc/ssl/certs/ca-bundle.crt"
                if name.startswith("Trusted HTTPS"):
                    certificate = work / "certificate.pem"
                result = run([args.fixture.resolve(), uri, "--reject" if rejection else "--play", certificate], env=environment)
                row = json.loads(result.stdout.strip().splitlines()[-1])
                if forbidden_fetch:
                    assert requests[forbidden_fetch] == before, f"Rejected request followed its target: {name}"
                if not rejection:
                    assert row["frames"] >= 15 and row["width"] == 320 and row["height"] == 180, (name, row)
                checks.append({"name": name, "passed": True, "result": row})
                print(name, "PASS", flush=True)
        finally:
            for server in (http, https):
                server.shutdown()
                server.server_close()
            for worker in workers:
                worker.join(timeout=5)
    report = {"passed": True, "scope": "Synthetic local HTTP(S)/HLS with production source policy and fakesink; no real AirPlay device or physical audio/GPU acceptance.", "checks": checks}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
