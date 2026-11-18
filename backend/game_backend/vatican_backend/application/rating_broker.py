from .ports import UnitOfWorkFactory
from ..domain.rating_broker import parse_result, quarantine_id


class AcceptRatingResult:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, body, property_id=None, correlation_id=None, content_type=None, content_encoding=None):
        metadata = [property_id, correlation_id, content_type, content_encoding]
        identity, digest = quarantine_id(body, metadata)
        try:
            result = parse_result(body, property_id, correlation_id, content_type, content_encoding)
        except ValueError:
            with self.uow() as work:
                work.rating_results.quarantine(identity, digest, 'InvalidResult')
            return
        with self.uow() as work:
            code = work.rating_results.accept(result)
            if code:
                work.rating_results.quarantine(identity, digest, code)
