"""Request examples survive JSON encoding and match the domain's strict allowlist."""
from fastapi.routing import APIRoute
from pydantic import TypeAdapter

from vatican_analytics.bootstrap.settings import Settings
from vatican_analytics.domain.events import EVENT_FIELDS, parse
from vatican_analytics.presentation.app import create_app
from vatican_analytics.presentation.documentation_models import AnalyticsMessage


def test_analytics_examples_async_statuses_and_security_match_the_api():
    app=create_app(Settings('postgresql://unused@localhost/test','s'*40,'clickhouse+http://default@127.0.0.1/default'))
    spec=app.openapi()
    for route in app.routes:
        if not isinstance(route,APIRoute):continue
        for method in route.methods:
            operation=spec['paths'][route.path][method.lower()]
            assert operation['description'] and operation['summary']
            assert bool(operation.get('security'))==(not route.path.startswith('/health/'))
            for status,response in operation['responses'].items():
                media=response.get('content',{}).get('application/json',{})
                assert media.get('schema'),(route.path,status)
                field=route.response_fields.get(int(status)) or route.response_field
                for sample in media.get('examples',{}).values():
                    _,errors=field.validate(sample['value'],{},loc=('response',))
                    assert not errors,(route.path,status,errors)
    operation=spec['paths']['/internal/messages']['post']
    assert {'200','202','409'}<=operation['responses'].keys()
    media=operation['requestBody']['content']['application/json']
    assert media['schema']['discriminator']['propertyName']=='eventName'
    assert set(EVENT_FIELDS)<=set(media['examples'])
    assert len({sample['value']['messageId'] for sample in media['examples'].values()})==len(media['examples'])
    for sample in media['examples'].values():
        value=sample['value']
        TypeAdapter(AnalyticsMessage).validate_python(value)
        parse(value['payload'],value['eventName'])
    assert operation['responses']['202']['content']['application/json']['examples']['pending']['value']['receipt'] is None
    assert spec['paths']['/internal/reports/daily']['get']['security']==[{'ServiceSecret':[]}]
