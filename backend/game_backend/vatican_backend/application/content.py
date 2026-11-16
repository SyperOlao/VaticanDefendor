import hashlib
import hmac
import json

from .errors import ApplicationError


class ContentUseCases:
    def __init__(self, uow, admin_secret):
        self.uow = uow
        self.admin_secret = admin_secret

    def publish(self, release_id, schema_version, payload_json, payload_sha256, body_release_id, credential):
        if not self.admin_secret or not credential or not hmac.compare_digest(self.admin_secret, credential):
            raise ApplicationError(401, "Content publisher credential required")
        if body_release_id != release_id or hashlib.sha256(payload_json.encode("utf-8")).hexdigest() != payload_sha256:
            raise ApplicationError(422, "Content identity or checksum mismatch")
        try:
            payload = json.loads(payload_json, parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
        except ValueError:
            raise ApplicationError(422, "Invalid content JSON") from None
        if not isinstance(payload, dict) or payload.get("_releaseId") != release_id or payload.get("_contentSchemaVersion") != schema_version:
            raise ApplicationError(422, "Content envelope does not match payload")
        with self.uow() as work:
            created = work.content.publish(id=release_id, schema_version=schema_version,
                                           payload_json=payload_json, payload_sha256=payload_sha256)
            if not created and work.content.get(release_id).payload_sha256 != payload_sha256:
                raise ApplicationError(409, "Published releases are immutable")
        return {"releaseId": release_id, "replayed": not created}

    def get(self, release_id):
        with self.uow() as work:
            row = work.content.get(release_id)
            if row is None:
                raise ApplicationError(404, "Content release not published")
            return {"releaseId": row.id, "schemaVersion": row.schema_version,
                    "payloadJson": row.payload_json, "payloadSha256": row.payload_sha256}
