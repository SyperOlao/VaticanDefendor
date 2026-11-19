"""Initialize the public media prefixes and upload bundled profile avatars."""
import argparse
import hashlib
from pathlib import Path

from botocore.exceptions import ClientError

from ..bootstrap.settings import Settings
from ..integrations.s3 import S3ImageStorage


def seed_avatars(storage, directory):
    images = sorted(directory.glob("*.png"))
    if not images:
        raise ValueError(f"No avatar PNGs found in {directory}")
    bucket = storage.settings.s3_bucket
    for path in images:
        key = f"avatars/{path.name}"
        content = path.read_bytes()
        storage.inspect(content, "image/png")
        digest = hashlib.sha256(content).hexdigest()
        try:
            storage.client.put_object(Bucket=bucket, Key=key, Body=content,
                ContentType="image/png", CacheControl="public, max-age=31536000, immutable",
                Metadata={"sha256": digest}, IfNoneMatch="*")
        except ClientError as error:
            if error.response.get("ResponseMetadata", {}).get("HTTPStatusCode") != 412:
                raise
            existing = storage.client.head_object(Bucket=bucket, Key=key)
            if existing.get("Metadata", {}).get("sha256") != digest:
                raise ValueError(f"Avatar object {key} already exists with different content") from error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--avatars", type=Path)
    args = parser.parse_args()
    storage = S3ImageStorage(Settings.from_environment())
    storage.initialize()
    if args.avatars is not None:
        seed_avatars(storage, args.avatars)
    print("Shop image bucket is ready:", storage.settings.s3_bucket)


if __name__ == "__main__":
    main()
