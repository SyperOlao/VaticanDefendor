import re

MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_IMAGE_EDGE = 4096
IMAGE_TYPES = {"image/png": ("PNG", "png"), "image/jpeg": ("JPEG", "jpg")}


def valid_image_key(value):
    return isinstance(value, str) and re.fullmatch(r"shop/items/[a-f0-9]{64}\.(png|jpg)", value) is not None
