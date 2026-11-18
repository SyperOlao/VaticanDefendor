"""Preserve literal null fields in documented auth responses."""
from copy import deepcopy

from fastapi.routing import APIRoute


def preserve_openapi_null_examples(app):
    original = app.openapi

    def openapi():
        if app.openapi_schema is None:
            spec = original()
            # FastAPI excludes None recursively while encoding its OpenAPI model.
            # For guests, sessionId: null is a required response field, not an omission.
            for route in app.routes:
                if not isinstance(route, APIRoute) or not route.include_in_schema:
                    continue
                for method in route.methods:
                    operation = spec['paths'][route.path_format][method.lower()]
                    for status, response in route.responses.items():
                        for media_type, content in response.get('content', {}).items():
                            if 'examples' in content:
                                operation['responses'][str(status)]['content'][media_type]['examples'] = deepcopy(content['examples'])
        return app.openapi_schema

    app.openapi = openapi
