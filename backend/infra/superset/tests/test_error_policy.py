import unittest

from error_policy import classify_chart_failure, sanitize_payload, install_chart_error_handling


class ChartErrorsTest(unittest.TestCase):
    def test_known_failures_and_retry_policy(self):
        for text, code, retryable in (
            ('Code: 60. UNKNOWN_TABLE SELECT secret', 'schema_unavailable', False),
            ('Code: 60. SELECT timeout, \'Code: 241\' FROM missing', 'schema_unavailable', False),
            ('Code: 47. UNKNOWN_IDENTIFIER', 'query_invalid', False),
            ('Code: 159. TIMEOUT_EXCEEDED', 'query_timeout', True),
            ('Code: 241. MEMORY_LIMIT_EXCEEDED', 'query_limit', False),
            ('Code: 516. Authentication failed', 'warehouse_access', False),
            ('Connection refused password=secret', 'dependency_unavailable', True),
            ('unexpected password=secret', 'chart_failed', False),
        ):
            with self.subTest(code=code):
                failure = classify_chart_failure(text)
                self.assertEqual((failure.code, failure.retryable), (code, retryable))
                self.assertNotIn('secret', failure.message)

    def test_partial_failure_preserves_successful_data(self):
        success = {'status':'success', 'data':[{'error':'business value', 'status':'failed'}], 'query':'SELECT 1'}
        bad = {'status':'failed','error':'Code: 60 password=secret', 'query':'SELECT secret',
               'stacktrace':'secret', 'exception':'secret', 'extra':{'connection':'secret'}}
        result, failed = sanitize_payload({'result':[success,bad]}, 200, 'reference-id')
        self.assertTrue(failed)
        self.assertEqual(result['result'][0], success)
        self.assertNotIn('secret', str(result['result'][1]))
        self.assertEqual(result['result'][1]['extra']['code'], 'schema_unavailable')

    def test_auth_and_non_json_failures_are_not_empty_results(self):
        for status, code in ((401,'session_expired'),(403,'permission_denied'),(429,'rate_limited'),
                             (500,'chart_failed'),(503,'dependency_unavailable')):
            result, failed = sanitize_payload(None, status, 'id')
            self.assertTrue(failed)
            self.assertEqual(result['errors'][0]['extra']['code'], code)

    def test_flask_response_contract_and_scope(self):
        from flask import Flask, jsonify
        app = Flask(__name__)
        install_chart_error_handling(app)

        @app.route('/api/v1/chart/data', methods=['POST'])
        def chart():
            return jsonify(errors=[{'message':'Code: 60. SELECT secret', 'extra':{'sql':'secret'}}]), 400

        @app.route('/other')
        def other():
            return jsonify(error='unchanged'), 400

        @app.route('/api/v1/chart/123/data/')
        def connection_failure():
            import logging
            try:
                raise ConnectionError('Connection refused password=secret')
            except ConnectionError:
                logging.getLogger('superset.views.error_handling').exception('Framework failure')
            return jsonify(message='Internal server error'), 500

        response = app.test_client().post('/api/v1/chart/data')
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.headers['Cache-Control'], 'no-store')
        self.assertNotIn('secret', response.text)
        self.assertIn(response.headers['X-Request-ID'], response.text)
        self.assertEqual(app.test_client().get('/other').json, {'error':'unchanged'})
        response = app.test_client().get('/api/v1/chart/123/data/')
        self.assertEqual(response.status_code,500)
        self.assertEqual(response.json['errors'][0]['extra']['code'],'dependency_unavailable')
        self.assertNotIn('secret',response.text)


if __name__ == '__main__':
    unittest.main()
