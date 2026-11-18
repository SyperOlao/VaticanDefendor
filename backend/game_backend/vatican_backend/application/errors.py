from .catalog import GameErrorCode


class ApplicationError(Exception):
    def __init__(self, status_code: int, detail, headers: dict | None = None, *, code: GameErrorCode | None = None):
        super().__init__(str(detail))
        self.status_code = status_code
        self.detail = detail
        self.headers = headers
        self.code = code
