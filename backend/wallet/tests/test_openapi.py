"""Swagger models and examples describe the actual wallet HTTP contract."""
from fastapi.routing import APIRoute
from vatican_wallet.bootstrap.settings import Settings
from vatican_wallet.presentation.app import create_app
from vatican_wallet.presentation.documentation import OPERATIONS


def test_wallet_responses_examples_security_and_operation_coverage():
    app=create_app(Settings('postgresql://unused@localhost/test','s'*40))
    spec=app.openapi()
    routes=[route for route in app.routes if isinstance(route,APIRoute)]
    assert {route.name for route in routes}==set(OPERATIONS)
    for route in routes:
        for method in route.methods:
            operation=spec['paths'][route.path][method.lower()]
            assert operation['summary'] and operation['description'] and operation['tags']
            assert bool(operation.get('security'))==not_health(route.path)
            for status,response in operation['responses'].items():
                media=response['content']['application/json']
                assert media['schema']
                field=route.response_fields.get(int(status)) or route.response_field
                for sample in media['examples'].values():
                    _,errors=field.validate(sample['value'],{},loc=('response',))
                    assert not errors,(route.path,status,errors)
    assert spec['paths']['/v1/wallet']['get']['security']==[{'BearerAuth':[]}]
    assert spec['paths']['/internal/charges']['post']['security']==[{'ServiceSecret':[]}]
    assert spec['paths']['/v1/wallet/transactions']['get']['responses']['200']['content']['application/json']['examples']['empty']['value']['nextCursor'] is None


def not_health(path):return not path.startswith('/health/')
