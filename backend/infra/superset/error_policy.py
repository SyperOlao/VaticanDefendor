"""Public chart errors: fixed wording, no SQL/credentials, no automatic retries."""
from dataclasses import dataclass
import logging
import re


@dataclass(frozen=True)
class ChartFailure:
    code: str
    message: str
    retryable: bool = False


def classify_chart_failure(text, status=400):
    if status == 401:
        return ChartFailure('session_expired', 'Сессия завершена. Войдите в Superset снова и обновите страницу.')
    if status == 403:
        return ChartFailure('permission_denied', 'Нет доступа к данным. Попросите администратора проверить права подключения и dataset.')
    lower = text.lower()
    if 'csrf' in lower:
        return ChartFailure('session_expired', 'Не удалось подтвердить сессию. Обновите страницу и повторите действие; при необходимости войдите снова.')
    if status == 429:
        return ChartFailure('rate_limited', 'Слишком много запросов. Подождите немного и обновите график.', True)
    match = re.search(r'\bCode:\s*(\d{1,4})\b', text, re.I)
    code = int(match.group(1)) if match else None
    # Match the server's code before inspecting prose which can include the SQL.
    if code in {159, 160, 209} or code is None and any(s in lower for s in ('timed out', 'timeout', 'time limit', 'timeout_error')):
        return ChartFailure('query_timeout', 'Запрос превысил время ожидания. Сузьте период или фильтр и обновите график.', True)
    if code == 241 or code is None and 'memory limit' in lower:
        return ChartFailure('query_limit', 'Запрос превысил лимит памяти. Сузьте период или число групп и повторите запрос.')
    if code in {516, 497} or code is None and any(s in lower for s in ('authentication failed', 'access denied', 'not enough privileges')):
        return ChartFailure('warehouse_access', 'Подключение к аналитике отклонено. Администратору нужно проверить учётные данные и права ClickHouse.')
    if code in {60, 81} or code is None and ('unknown_table' in lower or 'unknown_database' in lower):
        return ChartFailure('schema_unavailable', 'Источник или витрина не найдены. Откройте «Обновление BI»; администратору нужно проверить инициализацию схемы.')
    if code in {47, 62} or code is None and any(s in lower for s in ('unknown_identifier', 'syntax_error')):
        return ChartFailure('query_invalid', 'Запрос графика несовместим со схемой. Администратору нужно проверить колонки и настройки dataset; повтор без исправления не поможет.')
    if code == 210 or code is None and any(s in lower for s in ('connection refused', 'failed to establish', 'name resolution',
                                'connection reset', 'network is unreachable', 'max retries exceeded',
                                'name or service not known', 'server closed the connection',
                                'redis.exceptions.connectionerror')) or status in (502,503,504):
        return ChartFailure('dependency_unavailable', 'Сервис аналитики временно недоступен. Подождите и обновите график; если ошибка повторяется, проверьте ClickHouse, PostgreSQL и Redis.', True)
    return ChartFailure('chart_failed', 'Не удалось загрузить график. Обновите страницу; если ошибка повторяется, передайте администратору код обращения.')


def sanitize_payload(payload, status, request_id, failure_hint=None):
    """Preserve successful results and return the native Superset error envelope."""
    failed = False

    def public_failure(value):
        failure = classify_chart_failure(str(value), status)
        return failure_hint if failure.code == 'chart_failed' and failure_hint else failure

    def clean(value):
        nonlocal failed
        if isinstance(value, list):
            return [clean(item) for item in value]
        if not isinstance(value, dict):
            return value
        is_error = bool(value.get('error') or value.get('errors')) or value.get('status') in ('failed', 'error')
        if is_error:
            failed = True
            failure = public_failure(value)
            message = failure.message + ' Код обращения: ' + request_id
            extra = {'code': failure.code, 'retryable': failure.retryable, 'request_id': request_id}
            result = {key: clean(item) if key == 'result' else item for key, item in value.items()
                      if key in ('result', 'status', 'rowcount', 'result_type', 'result_format')}
            if 'error' in value:
                result['error'] = message
            if 'message' in value:
                result['message'] = message
            # A standard error object renders in both native chart and API clients.
            result['errors'] = [{'message': message, 'error_type': 'GENERIC_DB_ENGINE_ERROR',
                                 'level': 'error', 'extra': extra}]
            result['extra'] = extra
            return result
        return {key: clean(item) if key == 'result' else item for key, item in value.items()}

    result = clean(payload)
    if status >= 400 and not failed:
        failure = public_failure(payload)
        result = {'errors': [{'message': failure.message + ' Код обращения: ' + request_id,
                             'error_type': 'GENERIC_DB_ENGINE_ERROR', 'level': 'error',
                             'extra': {'code': failure.code, 'retryable': failure.retryable, 'request_id': request_id}}]}
        failed = True
    return result, failed


def is_chart_path(path):
    path = path.rstrip('/')
    return path in ('/api/v1/chart/data', '/superset/explore_json') or bool(re.fullmatch(r'/api/v1/chart/\d+/data', path))


class ChartFailureContext(logging.Filter):
    """Superset 6 masks uncaught connection errors before after_request runs.

    Its error-handling logger still receives the exception. Retain only a fixed
    classification in request-local state; neither the exception nor SQL is copied.
    """
    def filter(self, record):
        from flask import g, has_request_context, request
        if has_request_context() and is_chart_path(request.path) and record.exc_info and record.exc_info[1]:
            failure = classify_chart_failure(str(record.exc_info[1]), 500)
            if failure.code != 'chart_failed':
                g.bi_failure_hint = failure
        return True


def install_chart_error_handling(app):
    from uuid import uuid4
    from flask import g, request

    def chart_request():
        return is_chart_path(request.path)

    source_logger = logging.getLogger('superset.views.error_handling')
    if not any(isinstance(f, ChartFailureContext) for f in source_logger.filters):
        source_logger.addFilter(ChartFailureContext())

    @app.before_request
    def chart_request_id():
        if chart_request():
            g.bi_request_id = str(uuid4())

    @app.after_request
    def chart_error_response(response):
        if not chart_request():
            return response
        request_id = getattr(g, 'bi_request_id', str(uuid4()))
        response.headers['X-Request-ID'] = request_id
        payload = response.get_json(silent=True)
        if payload is None and response.status_code < 400:
            return response
        payload, failed = sanitize_payload(payload, response.status_code, request_id, getattr(g, 'bi_failure_hint', None))
        if failed:
            response.set_data(app.json.dumps(payload))
            response.content_type = 'application/json'
            response.headers['Cache-Control'] = 'no-store'
            logging.getLogger('vatican.bi').warning('Chart request failed request_id=%s status=%s', request_id, response.status_code)
        return response
