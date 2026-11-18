from dataclasses import replace
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from uuid import uuid4

from botocore.exceptions import ClientError, EndpointConnectionError
from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import pytest
from sqlalchemy import select

from vatican_shop.application.content import AdminCatalog, ReadCatalog
from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.media import AdminMedia
from vatican_shop.application.models import Principal
from vatican_shop.application.ports import ImageStorage, WeaponDefinitionsGateway
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.media import MAX_IMAGE_BYTES
from vatican_shop.integrations.s3 import S3ImageStorage
from vatican_shop.persistence.postgres.tables import ShopCatalogAudit
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.presentation.app import create_app

IMAGE = Path(__file__).resolve().parents[1] / "media/generated/sword-rare-v1.png"


class FakeS3:
    def __init__(self, database=None):
        self.database = database
        self.objects = {}
        self.down = False
        self.heads = 0

    def _check(self):
        if self.database:
            assert self.database.engine.pool.checkedout() == 0
        if self.down:
            raise EndpointConnectionError(endpoint_url="http://private-storage")

    def put_object(self, **values):
        self._check()
        self.objects[values["Key"]] = values

    def head_object(self, **values):
        self._check()
        self.heads += 1
        if values["Key"] not in self.objects:
            raise ClientError({"Error": {"Code": "404"}}, "HeadObject")
        row = self.objects[values["Key"]]
        return {"ContentType": row["ContentType"], "ContentLength": len(row["Body"]), "Metadata": row["Metadata"]}


@pytest.fixture
def media_world(shop_storage):
    database, url = shop_storage
    settings = Settings(url, "s" * 32, s3_endpoint_url="http://internal-minio:9000",
                        media_base_url="https://cdn.example.test/vatican-shop-media")
    fake = FakeS3(database)
    storage = S3ImageStorage(settings, client=fake)
    class Auth:
        def authenticate(self, token):
            return Principal(uuid4(), token == "guest", ("ADMIN",) if token in ("admin", "guest") else ())
    class Definitions:
        def get(self):
            assert database.engine.pool.checkedout() == 0
            return [{"Id": "sword.rare"}]
    auth, definitions = Auth(), Definitions()
    uow = PostgresUnitOfWorkFactory(database)
    return SimpleNamespace(database=database, settings=settings, fake=fake, storage=storage, auth=auth,
        definitions=definitions, admin=AdminCatalog(uow, auth, definitions, storage),
        reader=ReadCatalog(uow, definitions, storage), media=AdminMedia(auth, storage))


def content(**changes):
    return {"id": "sword", "weaponConfigurationId": "sword.rare", "title": "Sword", "price": 25,
            "purchaseLimit": 0, **changes}


def test_upload_hashes_image_and_sends_cache_metadata(media_world):
    w = media_world
    data = IMAGE.read_bytes()
    first = w.media.upload("admin", data, "image/png")
    assert w.media.upload("admin", data, "image/png") == first
    assert first["imageUrl"].startswith("https://cdn.example.test/")
    assert "internal-minio" not in first["imageUrl"]
    assert first["width"] == first["height"] == 1254
    assert w.fake.objects[first["imageKey"]]["CacheControl"].endswith("immutable")
    assert len(w.fake.objects) == 1


def test_lost_s3_put_ack_and_parallel_retries_keep_one_immutable_object(media_world, monkeypatch):
    w=media_world;data=IMAGE.read_bytes();original=w.fake.put_object
    def lost_ack(**values):
        original(**values)
        raise EndpointConnectionError(endpoint_url="http://private-storage")
    with monkeypatch.context() as patch:
        patch.setattr(w.fake,"put_object",lost_ack)
        with pytest.raises(ApplicationError) as error:
            w.media.upload("admin",data,"image/png")
        assert error.value.status_code==503
    assert len(w.fake.objects)==1
    with ThreadPoolExecutor(max_workers=4) as pool:
        responses=list(pool.map(lambda _:w.media.upload("admin",data,"image/png"),range(8)))
    assert all(response==responses[0] for response in responses)
    assert len(w.fake.objects)==1
    assert w.fake.objects[responses[0]["imageKey"]]["Body"]==data


@pytest.mark.parametrize("token", ["player", "guest"])
def test_media_use_case_requires_admin(media_world, token):
    with pytest.raises(ApplicationError) as error:
        media_world.media.upload(token, IMAGE.read_bytes(), "image/png")
    assert error.value.status_code == 403
    assert media_world.fake.objects == {}


@pytest.mark.parametrize("data,kind,status", [(b"<svg/>", "image/svg+xml", 422),
    (b"webp", "image/webp", 422),
    (b"not a PNG", "image/png", 422), (b"", "image/png", 422),
    (b"x" * (MAX_IMAGE_BYTES + 1), "image/png", 413)])
def test_invalid_binary_and_oversize_are_rejected(media_world, data, kind, status):
    with pytest.raises(ApplicationError) as error:
        media_world.media.upload("admin", data, kind)
    assert error.value.status_code == status
    assert not media_world.fake.objects


def test_mime_does_not_override_actual_image_format(media_world):
    with pytest.raises(ApplicationError) as error:
        media_world.media.upload("admin", IMAGE.read_bytes(), "image/jpeg")
    assert error.value.status_code == 422


def test_attaching_images_checks_s3_changes_catalog_and_preserves_old_put(media_world):
    w = media_world
    asset = w.media.upload("admin", IMAGE.read_bytes(), "image/png")
    original = w.reader.get()["version"]
    created = w.admin.create("admin", content(imageKey=asset["imageKey"]))
    assert created["imageUrl"] == asset["imageUrl"]
    catalog = w.reader.get()
    assert catalog["version"] != original and catalog["offers"][0]["imageUrl"] == asset["imageUrl"]
    w.fake.down = True
    # An old client omitting imageKey can edit the price during an S3 outage.
    updated = w.admin.update("admin", "sword", 1, content(price=30))
    assert updated["imageKey"] == asset["imageKey"]
    assert w.fake.heads == 1
    cleared = w.admin.update("admin", "sword", 2, content(price=30, imageKey=None))
    assert cleared["imageUrl"] is None
    assert asset["imageKey"] in w.fake.objects
    with w.database.transaction() as session:
        audit = list(session.scalars(select(ShopCatalogAudit).order_by(ShopCatalogAudit.created_at)))
        assert audit[-1].before_json["imageKey"] == asset["imageKey"]
        assert audit[-1].after_json["imageKey"] is None


@pytest.mark.parametrize("key", ["https://attacker.test/a.png", "../private/file.png", "other/key.png",
                                  "shop/items/" + "a" * 64 + ".png"])
def test_unuploaded_or_external_image_cannot_be_attached(media_world, key):
    with pytest.raises(ApplicationError) as error:
        media_world.admin.create("admin", content(imageKey=key))
    assert error.value.status_code == 422
    assert media_world.reader.get()["offers"] == []


def test_s3_outage_is_safe_and_does_not_commit_content(media_world):
    w = media_world
    w.fake.down = True
    with pytest.raises(ApplicationError) as error:
        w.media.upload("admin", IMAGE.read_bytes(), "image/png")
    assert error.value.status_code == 503 and "private-storage" not in error.value.detail
    with pytest.raises(ApplicationError) as error:
        w.admin.create("admin", content(imageKey="shop/items/" + "a" * 64 + ".png"))
    assert error.value.status_code == 503
    assert w.reader.get()["offers"] == []


def test_http_upload_and_catalog_media_fields(media_world):
    w = media_world
    class Overrides(Provider):
        @provide(scope=Scope.APP, provides=ImageStorage)
        def images(self):
            return w.storage
        @provide(scope=Scope.APP, provides=WeaponDefinitionsGateway)
        def definitions(self):
            return w.definitions
    with TestClient(create_app(w.settings, providers=(Overrides(),), auth_gateway=w.auth)) as client:
        data = IMAGE.read_bytes()
        path = "/v1/admin/shop/images"
        admin = {"Authorization": "Bearer admin"}
        assert client.post(path, content=data, headers={"Content-Type": "image/png"}).status_code == 401
        assert client.post(path, content=data, headers={"Content-Type": "image/png", "Authorization": "Bearer player"}).status_code == 403
        response = client.post(path, content=data, headers={**admin, "Content-Type": "image/png"})
        assert response.status_code == 201
        asset = response.json()
        assert client.post("/v1/admin/shop/offers", json=content(imageKey=asset["imageKey"]), headers=admin).status_code == 201
        result = client.get("/v1/shop", headers=admin).json()["offers"][0]
        assert result["imageUrl"] == asset["imageUrl"]
        body = {k: v for k, v in content(price=40).items() if k != "id"}
        updated = client.put("/v1/admin/shop/offers/sword", json={**body, "expectedRevision": 1}, headers=admin)
        assert updated.status_code == 200 and updated.json()["imageKey"] == asset["imageKey"]
        oversize = client.post(path, content=b"x" * (MAX_IMAGE_BYTES + 1), headers={**admin, "Content-Type": "image/png"})
        assert oversize.status_code == 413 and oversize.json()["code"] == "PayloadTooLarge"


def test_s3_credentials_are_not_in_settings_repr():
    settings = replace(Settings("secret-db", "secret-service"), s3_access_key="private-key", s3_secret_key="private-secret")
    assert "private-key" not in repr(settings) and "private-secret" not in repr(settings)


def test_invalid_s3_endpoint_is_a_safe_configuration_error():
    settings = Settings("unused", "s" * 32, s3_endpoint_url="http://bad_internal_hostname:9000",
                        s3_access_key="test", s3_secret_key="test-only-secret")
    with pytest.raises(ApplicationError) as error:
        S3ImageStorage(settings).upload(IMAGE.read_bytes(), "image/png")
    assert error.value.status_code == 503
    assert "bad_internal_hostname" not in error.value.detail
