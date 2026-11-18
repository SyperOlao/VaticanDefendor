"""Purchase source validation shared by every application entry point."""
import re
from uuid import UUID

from .catalogs import valid_offer_id


def validate_request(account_id, operation_id, offer_id, catalog_version):
    if (not all(isinstance(value, UUID) and value.int != 0 for value in (account_id, operation_id))
            or not valid_offer_id(offer_id) or not isinstance(catalog_version, str)
            or re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", catalog_version) is None):
        raise ValueError("Invalid purchase source")
