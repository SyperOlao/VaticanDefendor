import importlib
from uuid import uuid4
from dataclasses import dataclass
import pytest
from dishka import Provider,Scope,provide
from fastapi.testclient import TestClient
from vatican_inventory.application.ports import DefinitionsGateway as InventoryDefinitions
from vatican_shop.application.ports import DefinitionsGateway as ShopDefinitions
from vatican_backend.bootstrap.weapon_catalog import LoadCatalog


class OverrideDefinitions(Provider):
    @provide(scope=Scope.APP,provides=InventoryDefinitions)
    def definitions(self):
        class Definitions:
            def get(self):
                return LoadCatalog()
        return Definitions()


class OverrideCatalog(Provider):
    @provide(scope=Scope.APP,provides=ShopDefinitions)
    def definitions(self):
        class Catalog:
            def get(self):
                return {'version':'test','offers':[]}
        return Catalog()


@pytest.mark.parametrize('service,path',[('wallet','/v1/wallet'),('inventory','/v1/weapons'),('shop','/v1/shop'),('rating','/v1/leaderboards/missing')])
def test_service_http_boots_with_dishka_and_has_safe_errors(service,path,service_storage_factory):
    _,url=service_storage_factory(service)
    settings_type=importlib.import_module(f'vatican_{service}.bootstrap.settings').Settings
    create_app=importlib.import_module(f'vatican_{service}.presentation.app').create_app
    settings=settings_type(url,'s'*32)
    account=uuid4()
    class Auth:
        def authenticate(self,token):
            return type('Principal',(),{'account_id':account})()
    providers=(OverrideDefinitions(),) if service=='inventory' else (OverrideCatalog(),) if service=='shop' else ()
    with TestClient(create_app(settings,providers=providers,auth_gateway=Auth())) as client:
        assert client.get('/health/ready').status_code==200
        if service!='rating':
            denied=client.get(path)
            assert denied.status_code==401
            assert set(denied.json())=={'code','detail','requestId','errors'}
        response=client.get(path,headers={'Authorization':'Bearer test-token'})
        assert response.status_code==(404 if service=='rating' else 200)
        assert client.get('/openapi.json').status_code==200
        if service in ('wallet','inventory'):
            body={'messageId':str(uuid4()),'eventName':'invalid','schemaVersion':1,'payload':{}}
            assert client.post('/internal/messages',json=body).status_code==401
            assert client.post('/internal/messages',headers={'X-Service-Secret':'s'*32},json=body).status_code==422
