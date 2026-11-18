from .access import administrator
from .errors import ApplicationError
from .ports import AuthGateway, ImageStorage
from ..domain.media import IMAGE_TYPES, MAX_IMAGE_BYTES


class AdminMedia:
    def __init__(self, auth: AuthGateway, images: ImageStorage):
        self.auth, self.images = auth, images

    def upload(self, token: str, content: bytes, content_type: str):
        self.authorize(token)
        if len(content) > MAX_IMAGE_BYTES:
            raise ApplicationError(413, "Image exceeds 8 MiB")
        if not content or content_type not in IMAGE_TYPES:
            raise ApplicationError(422, "PNG or JPEG image required")
        return self.images.upload(content, content_type)

    def authorize(self, token):
        return administrator(self.auth, token)
