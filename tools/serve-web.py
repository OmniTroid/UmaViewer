#!/usr/bin/env python3
# Serve the WebGL build over HTTP. Adds the gzip content header for .gz files and the wasm
# media type, so both decompression-fallback (.unityweb) and raw-gzip (.gz) builds work.
# File System Access API needs a secure context; localhost qualifies.
#
#   python3 tools/serve-web.py [port]   # default 8000, serves Build/Web
import os, sys, http.server, socketserver

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Build", "Web")

class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def end_headers(self):
        if self.path.endswith(".gz"):
            self.send_header("Content-Encoding", "gzip")
        if self.path.endswith(".wasm") or self.path.endswith(".wasm.gz"):
            self.send_header("Content-Type", "application/wasm")
        super().end_headers()

def main():
    if not os.path.isdir(ROOT):
        sys.exit(f"No build at {ROOT}. Run tools/build-web.sh first.")
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    with socketserver.TCPServer(("", port), Handler) as httpd:
        print(f"Serving {ROOT} at http://localhost:{port}")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass

if __name__ == "__main__":
    main()
