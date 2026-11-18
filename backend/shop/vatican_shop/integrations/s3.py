import hashlib
from io import BytesIO
import json
import warnings

import boto3
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError
from PIL import Image, UnidentifiedImageError

from ..application.errors import ApplicationError
from ..domain.media import IMAGE_TYPES, MAX_IMAGE_BYTES, MAX_IMAGE_EDGE, valid_image_key


class S3ImageStorage:
    def __init__(self, settings, *, client=None):
        self.settings = settings
        self._client = client

    @property
    def client(self):
        if self._client is None:
            if not self.settings.s3_access_key or not self.settings.s3_secret_key:
                raise ApplicationError(503, "Image storage is not configured")
            try:
                self._client = boto3.client("s3", endpoint_url=self.settings.s3_endpoint_url,
                    aws_access_key_id=self.settings.s3_access_key, aws_secret_access_key=self.settings.s3_secret_key,
                    region_name=self.settings.s3_region, config=Config(signature_version="s3v4",
                        s3={"addressing_style": "path"}, connect_timeout=2, read_timeout=5,
                        retries={"max_attempts": 2, "mode": "standard"}))
            except (ValueError, BotoCoreError):
                raise ApplicationError(503, "Image storage configuration is invalid") from None
        return self._client

    def url(self, key):
        if not valid_image_key(key):
            raise ApplicationError(422, "Invalid image key")
        return self.settings.media_base_url.rstrip("/") + "/" + key

    @staticmethod
    def inspect(content, content_type):
        if not content or len(content) > MAX_IMAGE_BYTES or content_type not in IMAGE_TYPES:
            raise ApplicationError(422, "Invalid image")
        try:
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(BytesIO(content)) as image:
                    width, height = image.size
                    if (image.format != IMAGE_TYPES[content_type][0] or getattr(image, "n_frames", 1) != 1
                            or not 1 <= width <= MAX_IMAGE_EDGE or not 1 <= height <= MAX_IMAGE_EDGE):
                        raise ValueError()
                    image.verify()
                # PNG chunk checks alone do not guarantee that the pixel stream decodes.
                with Image.open(BytesIO(content)) as image:
                    image.load()
        except (OSError, UnidentifiedImageError, ValueError, SyntaxError,
                Image.DecompressionBombWarning, Image.DecompressionBombError):
            raise ApplicationError(422, "Invalid or unsupported image") from None
        return width, height

    def upload(self, content, content_type):
        width, height = self.inspect(content, content_type)
        digest = hashlib.sha256(content).hexdigest()
        key = "shop/items/" + digest + "." + IMAGE_TYPES[content_type][1]
        try:
            self.client.put_object(Bucket=self.settings.s3_bucket, Key=key, Body=content,
                ContentType=content_type, CacheControl="public, max-age=31536000, immutable",
                Metadata={"sha256": digest, "width": str(width), "height": str(height)})
        except (BotoCoreError, ClientError):
            raise ApplicationError(503, "Image storage is unavailable") from None
        return {"imageKey": key, "imageUrl": self.url(key), "contentType": content_type,
                "sizeBytes": len(content), "width": width, "height": height}

    def require(self, key):
        if not valid_image_key(key):
            raise ApplicationError(422, "Invalid image key")
        try:
            result = self.client.head_object(Bucket=self.settings.s3_bucket, Key=key)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") in ("404", "NoSuchKey", "NotFound"):
                raise ApplicationError(422, "Image was not uploaded") from None
            raise ApplicationError(503, "Image storage is unavailable") from None
        except BotoCoreError:
            raise ApplicationError(503, "Image storage is unavailable") from None
        content_type = result.get("ContentType")
        if (content_type not in IMAGE_TYPES or not 1 <= result.get("ContentLength", 0) <= MAX_IMAGE_BYTES
                or not key.endswith("." + IMAGE_TYPES[content_type][1])
                or result.get("Metadata", {}).get("sha256") != key.rsplit("/", 1)[-1].split(".")[0]):
            raise ApplicationError(422, "Object is not a shop image")

    def initialize(self):
        try:
            try:
                self.client.head_bucket(Bucket=self.settings.s3_bucket)
            except ClientError as error:
                if error.response.get("Error", {}).get("Code") not in ("404", "NoSuchBucket", "NotFound"):
                    raise
                arguments = {"Bucket": self.settings.s3_bucket}
                if self.settings.s3_region != "us-east-1":
                    arguments["CreateBucketConfiguration"] = {"LocationConstraint": self.settings.s3_region}
                self.client.create_bucket(**arguments)
            policy = {"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": "*",
                "Action": ["s3:GetObject"], "Resource": [f"arn:aws:s3:::{self.settings.s3_bucket}/shop/items/*"]}]}
            self.client.put_bucket_policy(Bucket=self.settings.s3_bucket, Policy=json.dumps(policy))
        except (BotoCoreError, ClientError):
            raise ApplicationError(503, "Image storage initialization failed") from None
