"""One public error contract; exception messages, SQL and input values stay private."""
from dataclasses import dataclass, field
from http import HTTPStatus
import logging
from pathlib import Path
import re
import traceback
from uuid import UUID, uuid4

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError, ResponseValidationError
from fastapi.responses import JSONResponse
from sqlalchemy.exc import DBAPIError, IntegrityError, InterfaceError, OperationalError, SQLAlchemyError, TimeoutError
from starlette.datastructures import Headers, MutableHeaders
from starlette.exceptions import HTTPException
from starlette.requests import ClientDisconnect
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from ..application.errors import ApplicationError

logger = logging.getLogger("vatican_wallet.errors")


class SchemaNotReady(Exception):
    pass


class ResponseAlreadyStartedError(RuntimeError):
    """Safe replacement for an exception which cannot be returned as JSON anymore."""


@dataclass(frozen=True)
class ErrorSpec:
    status: int
    code: str
    detail: str
    headers: dict[str, str] = field(default_factory=dict)
    errors: list[dict] = field(default_factory=list)


_VALIDATION_MESSAGES = {
    "missing": "Required field is missing", "extra_forbidden": "Unexpected field",
    "json_invalid": "Malformed JSON", "string_type": "Must be a string",
    "string_too_short": "Value is too short", "string_too_long": "Value is too long",
    "string_pattern_mismatch": "Invalid format", "uuid_parsing": "Invalid UUID",
    "uuid_type": "Invalid UUID", "int_parsing": "Must be an integer", "int_type": "Must be an integer",
    "bool_parsing": "Must be a boolean", "bool_type": "Must be a boolean",
    "list_type": "Must be a list", "model_attributes_type": "Must be an object",
    "model_type": "Must be an object", "literal_error": "Invalid choice",
    "greater_than": "Value is outside the allowed range", "greater_than_equal": "Value is outside the allowed range",
    "less_than": "Value is outside the allowed range", "less_than_equal": "Value is outside the allowed range",
    "too_long": "Value is too long", "too_short": "Value is too short",
}


def _known_field_names() -> set[str]:
    return {"body", "query", "path", "header", "cookie", "server_id", "run_id", "release_id",
            "contentReleaseId", "protocolVersion", "transport", "runId", "generation", "schemaVersion",
            "simulationTick", "snapshotJson", "sha256", "accountId", "outcome", "churchLevel",
            "expectedVersion", "displayName", "releaseId", "payloadJson", "payloadSha256"}


def validation_issues(error: RequestValidationError) -> list[dict]:
    issues = []
    known_fields = _known_field_names()
    for entry in error.errors()[:50]:
        kind = entry.get("type", "invalid")
        # Custom validators can embed input in both their messages and their locations.
        parts = []
        for part in entry.get("loc", ()):
            if isinstance(part, int) and 0 <= part <= 1000000:
                parts.append(str(part))
            elif isinstance(part, str) and part in known_fields:
                parts.append(part)
            else:
                break
        if kind == "extra_forbidden":
            parts = parts[:1]
        issues.append({
            "field": ".".join(parts) or "request",
            "type": kind if kind in _VALIDATION_MESSAGES else "invalid",
            "message": _VALIDATION_MESSAGES.get(kind, "Invalid value"),
        })
    return issues


def _sqlstate(error: DBAPIError) -> str | None:
    value = getattr(error.orig, "sqlstate", None)
    return value if isinstance(value, str) and re.fullmatch(r"[0-9A-Z]{5}", value) else None


def _internal() -> ErrorSpec:
    return ErrorSpec(500, "InternalError", "An internal error occurred")


def _database_error(error: SQLAlchemyError) -> ErrorSpec:
    state = _sqlstate(error) if isinstance(error, DBAPIError) else None
    if state in {"40001", "40P01"}:
        return ErrorSpec(409, "ConcurrentModification", "Another operation conflicted with this request; retry the operation")
    if state in {"42P01", "42703"}:
        return ErrorSpec(503, "SchemaNotReady", "Wallet schema is not ready", {"Retry-After": "1"})
    if state == "57014":
        return ErrorSpec(503, "OperationTimeout", "The database operation timed out", {"Retry-After": "1"})
    disconnected = isinstance(error, DBAPIError) and error.connection_invalidated
    unavailable = isinstance(error, OperationalError) and (
        state is None or state.startswith(("08", "53", "57P")) or state in {"28P01", "28000", "3D000"}
    )
    if disconnected or unavailable or isinstance(error, (TimeoutError, InterfaceError)):
        return ErrorSpec(503, "DatabaseUnavailable", "Wallet database is temporarily unavailable", {"Retry-After": "1"})
    # Constraint bugs, invalid queries and broken ORM state are server errors, not retryable outages.
    return _internal()


def classify_error(error: Exception) -> ErrorSpec:
    while isinstance(error, ExceptionGroup) and len(error.exceptions) == 1:
        error = error.exceptions[0]
    if isinstance(error, ApplicationError):
        codes = {401: "InvalidCredentials", 403: "PermissionDenied", 404: "ResourceNotFound",
                 409: "ResourceConflict", 422: "InvalidRequest", 503: "ServiceUnavailable"}
        headers = {"WWW-Authenticate": "Bearer"} if error.status_code == 401 else {}
        if error.status_code == 503:
            headers["Retry-After"] = "1"
        return ErrorSpec(error.status_code, codes.get(error.status_code, "ApplicationError"),
                         error.detail if isinstance(error.detail, str) else "Request could not be completed", headers)
    if isinstance(error, RequestValidationError):
        return ErrorSpec(422, "InvalidRequest", "Request validation failed", errors=validation_issues(error))
    if isinstance(error, SchemaNotReady):
        return ErrorSpec(503, "SchemaNotReady", "Wallet schema is not ready", {"Retry-After": "1"})
    if isinstance(error, SQLAlchemyError):
        return _database_error(error)
    if isinstance(error, HTTPException):
        if error.status_code >= 500:
            if error.status_code == 503:
                return ErrorSpec(503, "ServiceUnavailable", "Service is temporarily unavailable", {"Retry-After": "1"})
            return _internal()
        codes = {400: "InvalidRequest", 401: "InvalidCredentials", 403: "PermissionDenied",
                 404: "RouteNotFound", 405: "MethodNotAllowed", 413: "RequestTooLarge",
                 415: "UnsupportedMediaType", 422: "InvalidRequest", 429: "RateLimitExceeded"}
        safe_headers = {}
        supplied_headers = {key.lower(): value for key, value in (error.headers or {}).items()}
        if error.status_code == 405:
            methods = [method.strip() for method in supplied_headers.get("allow", "").split(",")]
            if methods and all(method in {"GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE", "CONNECT"}
                               for method in methods):
                safe_headers["Allow"] = ", ".join(methods)
        retry = supplied_headers.get("retry-after", "")
        if error.status_code == 429 and retry.isascii() and retry.isdecimal() and len(retry) <= 5 and 1 <= int(retry) <= 86400:
            safe_headers["Retry-After"] = retry
        if error.status_code == 401:
            safe_headers["WWW-Authenticate"] = "Bearer"
        try:
            detail = HTTPStatus(error.status_code).phrase
        except ValueError:
            detail = "Request could not be completed"
        return ErrorSpec(error.status_code, codes.get(error.status_code, "HttpError"), detail, safe_headers)
    return _internal()


def _request_id(scope: Scope) -> str:
    incoming = Headers(scope=scope).get("x-request-id", "")
    try:
        return str(UUID(incoming))
    except (ValueError, AttributeError):
        return str(uuid4())


def _log_failure(scope: Scope, error: Exception, spec: ErrorSpec) -> None:
    if spec.status < 500:
        return
    request_id = scope.get("state", {}).get("request_id", "unknown")
    # Never pass exc_info or format_exception: their messages/locals may contain SQL parameters or tokens.
    frames = [f"{Path(frame.filename).name}:{frame.lineno}:{frame.name}"
              for frame in traceback.extract_tb(error.__traceback__)[-20:]]
    log = logger.error if spec.status >= 500 and spec.status != 503 else logger.warning
    log("Wallet request failed request_id=%s code=%s exception=%s frames=%s", request_id, spec.code,
        type(error).__name__, " > ".join(frames),
        extra={"request_id": request_id, "error_code": spec.code, "status_code": spec.status,
               "exception_type": type(error).__name__, "trace_frames": frames})


def error_response(scope: Scope, error: Exception) -> JSONResponse:
    spec = classify_error(error)
    _log_failure(scope, error, spec)
    request_id = scope.setdefault("state", {}).setdefault("request_id", str(uuid4()))
    return JSONResponse(status_code=spec.status, content={
        "code": spec.code, "detail": spec.detail, "requestId": request_id, "errors": spec.errors,
    }, headers={**spec.headers, "X-Request-ID": request_id, "Cache-Control": "no-store", "Pragma": "no-cache"})


async def handle_error(request: Request, error: Exception):
    return error_response(request.scope, error)


class ErrorBoundaryMiddleware:
    def __init__(self, app: ASGIApp):
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        request_id = _request_id(scope)
        scope.setdefault("state", {})["request_id"] = request_id
        started = False

        async def send_with_context(message: Message):
            nonlocal started
            if message["type"] == "http.response.start":
                started = True
                headers = MutableHeaders(scope=message)
                headers["X-Request-ID"] = request_id
                headers["Cache-Control"] = "no-store"
                headers["Pragma"] = "no-cache"
            await send(message)

        try:
            await self.app(scope, receive, send_with_context)
        except ClientDisconnect:
            return
        except Exception as error:
            if started:
                _log_failure(scope, error, _internal())
                # The response can no longer be replaced; only the sanitized failure reaches server logs.
                raise ResponseAlreadyStartedError(f"Response failed; request_id={request_id}") from None
            await error_response(scope, error)(scope, receive, send_with_context)


def install_error_handling(app: FastAPI) -> None:
    for error_type in (ApplicationError, RequestValidationError, ResponseValidationError, SQLAlchemyError, HTTPException, SchemaNotReady):
        app.add_exception_handler(error_type, handle_error)
    app.add_middleware(ErrorBoundaryMiddleware)

