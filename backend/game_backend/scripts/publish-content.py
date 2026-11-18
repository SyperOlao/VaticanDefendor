"""Publish an exported Unity ContentRelease. Credentials come only from the environment."""
import argparse
import json
import os
from pathlib import Path
from urllib.parse import urlsplit

import httpx

parser = argparse.ArgumentParser()
parser.add_argument("publication", type=Path)
parser.add_argument("--allow-local-http", action="store_true")
args = parser.parse_args()
base = os.environ["VD_BACKEND_URL"].rstrip("/")
parsed = urlsplit(base)
if parsed.scheme != "https" and not (args.allow_local_http and parsed.scheme == "http" and parsed.hostname in ("127.0.0.1", "localhost", "::1")):
    parser.error("HTTPS is required; local HTTP needs --allow-local-http")
body = json.loads(args.publication.read_text(encoding="utf-8"))
with httpx.Client(timeout=30) as client:
    response = client.put(base + "/internal/content/" + body["releaseId"], json=body,
                          headers={"X-Content-Admin-Secret": os.environ["VD_CONTENT_ADMIN_SECRET"]})
    response.raise_for_status()
print("Published", body["releaseId"])
