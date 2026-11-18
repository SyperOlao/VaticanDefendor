from enum import StrEnum


class ProjectionStatus(StrEnum):
    PENDING = 'PENDING'
    COMPLETED = 'COMPLETED'
    REVIEW = 'REVIEW'


class MessageStatus(StrEnum):
    APPLIED = 'APPLIED'
    PENDING = 'PENDING'
    REVIEW = 'REVIEW'
