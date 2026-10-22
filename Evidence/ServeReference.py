from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

Reference = Path('C:/Users/sgsgg/Downloads/MergedVersion (2).html')

class ReferenceServer(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/':
            self.send_error(404)
            return
        Content = Reference.read_bytes()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(Content)))
        self.end_headers()
        self.wfile.write(Content)

HTTPServer(('127.0.0.1', 8766), ReferenceServer).serve_forever()
