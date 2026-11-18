"""HTTP adapters for immutable content publication."""
from fastapi import Header, Response
from pydantic import BaseModel, ConfigDict, Field
from .openapi_examples import PUBLICATION


class Publication(BaseModel):
    model_config = ConfigDict(extra="forbid",json_schema_extra={"examples":[PUBLICATION]})
    releaseId: str = Field(pattern=r"^sha256-[0-9a-f]{64}$",description="ID релиза; должен совпадать с URL и _releaseId внутри payloadJson.")
    schemaVersion: int = Field(ge=1,description="Версия контентного формата Unity.")
    payloadJson: str = Field(min_length=2, max_length=2_000_000,description="Строка экспортированного Unity ContentRelease. Пример — сокращённая оболочка, не готовый игровой контент.")
    payloadSha256: str = Field(pattern=r"^[0-9a-f]{64}$",description="SHA-256 точных UTF-8 байтов payloadJson, lowercase hex.")

