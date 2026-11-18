"""Upload local item art through the administrator HTTP API, without local S3 credentials."""
import argparse
import getpass
import json
from pathlib import Path

import httpx

from ..domain.media import MAX_IMAGE_BYTES


def main():
    parser = argparse.ArgumentParser(description="Upload PNG/JPEG files as an auth administrator")
    parser.add_argument("files", nargs="+", type=Path)
    parser.add_argument("--url", default="http://127.0.0.1:8084")
    args = parser.parse_args()
    token = getpass.getpass("Auth administrator access token: ")
    types = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}
    try:
        with httpx.Client(timeout=30, trust_env=False, follow_redirects=False) as client:
            for file in args.files:
                kind = types.get(file.suffix.lower())
                if kind is None or not 0 < file.stat().st_size <= MAX_IMAGE_BYTES:
                    parser.exit(1, "Expected PNG/JPEG files up to 8 MiB\n")
                response = client.post(args.url.rstrip("/") + "/v1/admin/shop/images", content=file.read_bytes(),
                    headers={"Authorization": "Bearer " + token, "Content-Type": kind})
                if response.status_code != 201:
                    parser.exit(1, "Image upload was not acknowledged; HTTP " + str(response.status_code) + "\n")
                print(json.dumps({"file": file.name, **response.json()}, ensure_ascii=False))
    except (OSError, httpx.HTTPError, ValueError, TypeError):
        parser.exit(1, "Image file or shop service is unavailable\n")


if __name__ == "__main__":
    main()
