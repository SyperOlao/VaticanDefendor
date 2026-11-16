"""HTTP adapters for immutable content publication."""
from fastapi import Header, Response
from pydantic import BaseModel, ConfigDict, Field


class Publication(BaseModel):
    model_config = ConfigDict(extra="forbid")
    releaseId: str = Field(pattern=r"^sha256-[0-9a-f]{64}$")
    schemaVersion: int = Field(ge=1)
    payloadJson: str = Field(min_length=2, max_length=2_000_000)
    payloadSha256: str = Field(pattern=r"^[0-9a-f]{64}$")


def install_content(app, use_cases):
    @app.put("/internal/content/{release_id}")
    def publish(release_id: str, body: Publication, x_content_admin_secret: str | None = Header(default=None)):
        return use_cases.publish(release_id, body.schemaVersion, body.payloadJson, body.payloadSha256,
                                 body.releaseId, x_content_admin_secret)

    @app.get("/v1/content/{release_id}")
    def get_content(release_id: str, response: Response):
        result = use_cases.get(release_id)
        response.headers["Cache-Control"] = "public, max-age=31536000, immutable"
        response.headers["ETag"] = '"' + result["payloadSha256"] + '"'
        return result
