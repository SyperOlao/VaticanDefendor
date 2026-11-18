from uuid import UUID

from .errors import ApplicationError


def identifier(value):
    if not isinstance(value, UUID):
        raise ApplicationError(422, "Invalid identifier")


def revision(value):
    if value is not None and (type(value) is not int or not 1 <= value <= 9_223_372_036_854_775_807):
        raise ApplicationError(422, "Invalid inventory revision")
