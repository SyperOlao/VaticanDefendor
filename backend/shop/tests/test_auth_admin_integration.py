"""Exercise shop role checks against real opaque sessions from the auth service."""
import os
from pathlib import Path
import sys
import hashlib
import json
from dataclasses import replace
from uuid import uuid4

import httpx
import pytest
from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "auth"))
from vatican_auth.application.access import BootstrapAdminUseCase
from vatican_auth.bootstrap.settings import AuthSettings
from vatican_auth.migrate import migrate as auth_migrate
from vatican_auth.presentation.app import create_app as auth_app

from vatican_shop.application.ports import WeaponDefinitionsGateway
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.integrations.http import AuthHttpGateway
from vatican_shop.presentation.app import create_app


@pytest.mark.parametrize("live_s3", [False, True])
def test_only_actual_auth_administrator_can_manage_shop(shop_storage, live_s3):
    if live_s3 and not os.getenv("VD_TEST_SHOP_S3_ENDPOINT_URL"):
        pytest.skip("Set VD_TEST_SHOP_S3_* to verify actual image uploads")
    database, shop_url = shop_storage
    admin_url = make_url(os.environ["VD_TEST_DATABASE_URL"]).set(drivername="postgresql+psycopg")
    engine = create_engine(admin_url)
    schema = "shop_auth_integration_" + uuid4().hex
    with engine.begin() as connection:
        connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    url = admin_url.update_query_dict({"options": "-c search_path=" + schema}).render_as_string(hide_password=False)
    try:
        auth_migrate(url)
        with TestClient(auth_app(AuthSettings(url)), client=("127.0.0.1", 12345)) as auth_client:
            def register(login):
                response = auth_client.post("/v1/auth/register", json={"login": login, "password": "test-password-123"})
                assert response.status_code == 201
                return response.json()["accessToken"]
            player_token = register("shop_player")
            admin_token = register("shop_admin")
            async def bootstrap():
                async with auth_client.app.state.dishka_container() as container:
                    await (await container.get(BootstrapAdminUseCase)).execute("shop_admin")
            auth_client.portal.call(bootstrap)

            def handler(request):
                assert database.engine.pool.checkedout() == 0
                reply = auth_client.get(request.url.path, headers=dict(request.headers))
                return httpx.Response(reply.status_code, json=reply.json())

            class DefinitionsProvider(Provider):
                @provide(scope=Scope.APP, provides=WeaponDefinitionsGateway)
                def definitions(self):
                    class Definitions:
                        def get(self):
                            return [{"Id": kind + ".rare"} for kind in ("sword", "spear", "axe")]
                    return Definitions()

            with httpx.Client(transport=httpx.MockTransport(handler)) as transport:
                settings = Settings(shop_url, "s" * 32)
                if live_s3:
                    settings = replace(settings, s3_endpoint_url=os.environ["VD_TEST_SHOP_S3_ENDPOINT_URL"],
                        s3_bucket=os.environ["VD_TEST_SHOP_S3_BUCKET"],
                        s3_access_key=os.environ["VD_TEST_SHOP_S3_ACCESS_KEY"],
                        s3_secret_key=os.environ["VD_TEST_SHOP_S3_SECRET_KEY"],
                        media_base_url=os.environ["VD_TEST_SHOP_MEDIA_BASE_URL"])
                app = create_app(settings, providers=(DefinitionsProvider(),),
                                 auth_gateway=AuthHttpGateway(transport, "http://auth"))
                with TestClient(app) as client:
                    path = "/v1/admin/shop/offers"
                    assert client.get(path, headers={"Authorization": "Bearer " + player_token}).status_code == 403
                    headers = {"Authorization": "Bearer " + admin_token}
                    body = {"id": "sword", "weaponConfigurationId": "sword.rare", "title": "Sword", "price": 25}
                    response = client.post(path, json=body, headers=headers)
                    assert response.status_code == 201
                    assert client.get(path, headers=headers).json()["offers"][0]["id"] == "sword"
                    if live_s3:
                        report = []
                        for kind in ("sword", "spear", "axe"):
                            file = Path(__file__).resolve().parents[1] / f"media/generated/{kind}-rare-v1.png"
                            image = file.read_bytes()
                            uploaded = client.post("/v1/admin/shop/images", content=image,
                                headers={**headers, "Content-Type": "image/png"})
                            assert uploaded.status_code == 201
                            asset = uploaded.json()
                            assert hashlib.sha256(image).hexdigest() in asset["imageKey"]
                            public = httpx.get(asset["imageUrl"], headers={"Origin": "http://127.0.0.1:8084"}, trust_env=False)
                            assert public.status_code == 200 and public.content == image
                            assert public.headers["content-type"] == "image/png"
                            assert "immutable" in public.headers["cache-control"]
                            assert public.headers.get("access-control-allow-origin") in ("*", "http://127.0.0.1:8084")
                            product = {"weaponConfigurationId": kind + ".rare", "title": kind, "price": 25,
                                       "imageKey": asset["imageKey"]}
                            if kind == "sword":
                                attached = client.put(path + "/sword", headers=headers,
                                    json={**product, "expectedRevision": 1})
                            else:
                                attached = client.post(path, headers=headers, json={**product, "id": kind})
                            assert attached.status_code in (200, 201)
                            assert attached.json()["imageUrl"] == asset["imageUrl"]
                            report.append({"item": kind, **asset})
                        catalog = client.get("/v1/shop", headers=headers)
                        assert catalog.status_code == 200
                        assert {o["imageUrl"] for o in catalog.json()["offers"]} == {a["imageUrl"] for a in report}
                        denied = client.post("/v1/admin/shop/images", content=image, headers={
                            "Authorization": "Bearer " + player_token, "Content-Type": "image/png"})
                        assert denied.status_code == 403
                        # Anonymous writes and bucket listing must remain disabled.
                        assert httpx.put(asset["imageUrl"], content=b"denied", trust_env=False).status_code == 403
                        assert httpx.get(settings.media_base_url + "?list-type=2", trust_env=False).status_code == 403
                        report_file = os.getenv("VD_TEST_SHOP_MEDIA_REPORT_FILE")
                        if report_file:
                            Path(report_file).write_text(json.dumps({"images": report, "catalog": catalog.json(),
                                "verified": ["admin upload", "public GET", "CORS", "catalog imageUrl", "anonymous write denied"]},
                                ensure_ascii=False, indent=2) + "\n")
                    assert auth_client.post("/v1/auth/logout", headers=headers).status_code == 204
                    assert client.delete(path + "/sword?expectedRevision=1", headers=headers).status_code == 401
    finally:
        with engine.begin() as connection:
            connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        engine.dispose()
