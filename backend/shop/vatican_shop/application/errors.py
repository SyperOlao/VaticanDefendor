class ApplicationError(Exception):
    def __init__(self, status_code: int, detail, headers: dict | None = None):
        super().__init__(str(detail))
        self.status_code = status_code
        self.detail = detail
        self.headers = headers


class ParticipantConflict(ApplicationError):
    """The participant did not establish whether the requested effect was applied."""

    def __init__(self):
        super().__init__(409, "Purchase participant requires reconciliation")
