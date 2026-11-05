"""Immutable content publication; only a deployment credential can publish."""
import hashlib
import hmac
import json

from fastapi import Header, HTTPException, Response
from pydantic import BaseModel, ConfigDict, Field


class Publication(BaseModel):
    model_config = ConfigDict(extra="forbid")
    releaseId: str = Field(pattern=r"^sha256-[0-9a-f]{64}$")
    schemaVersion: int = Field(ge=1)
    payloadJson: str = Field(min_length=2, max_length=2_000_000)
    payloadSha256: str = Field(pattern=r"^[0-9a-f]{64}$")


def install_content(app, connect, admin_secret):
    @app.put("/internal/content/{release_id}")
    def publish(release_id: str, body: Publication, x_content_admin_secret: str | None = Header(default=None)):
        if not admin_secret or not x_content_admin_secret or not hmac.compare_digest(admin_secret, x_content_admin_secret):
            raise HTTPException(401, "Content publisher credential required")
        if body.releaseId != release_id or hashlib.sha256(body.payloadJson.encode("utf-8")).hexdigest() != body.payloadSha256:
            raise HTTPException(422, "Content identity or checksum mismatch")
        try:
            payload = json.loads(body.payloadJson, parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
        except ValueError:
            raise HTTPException(422, "Invalid content JSON") from None
        if not isinstance(payload, dict) or payload.get("_releaseId") != release_id or payload.get("_contentSchemaVersion") != body.schemaVersion:
            raise HTTPException(422, "Content envelope does not match payload")
        with connect() as db:
            row = db.execute("INSERT INTO content_releases(id,schema_version,payload_json,payload_sha256) VALUES (%s,%s,%s,%s) "
                             "ON CONFLICT(id) DO NOTHING RETURNING id",
                             (release_id, body.schemaVersion, body.payloadJson, body.payloadSha256)).fetchone()
            if row is None:
                prior = db.execute("SELECT payload_sha256 FROM content_releases WHERE id=%s", (release_id,)).fetchone()
                if prior["payload_sha256"] != body.payloadSha256:
                    raise HTTPException(409, "Published releases are immutable")
        return {"releaseId": release_id, "replayed": row is None}

    @app.get("/v1/content/{release_id}")
    def get_content(release_id: str, response: Response):
        with connect() as db:
            row = db.execute("SELECT * FROM content_releases WHERE id=%s", (release_id,)).fetchone()
        if row is None:
            raise HTTPException(404, "Content release not published")
        response.headers["Cache-Control"] = "public, max-age=31536000, immutable"
        response.headers["ETag"] = '"' + row["payload_sha256"] + '"'
        return {"releaseId": row["id"], "schemaVersion": row["schema_version"],
                "payloadJson": row["payload_json"], "payloadSha256": row["payload_sha256"]}
