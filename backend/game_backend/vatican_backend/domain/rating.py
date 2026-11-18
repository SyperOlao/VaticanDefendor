"""Acknowledgement contract for a run's immutable XP contribution."""
from enum import StrEnum
from uuid import UUID, uuid5

RATING_EVENT = 'run.rating'
MAX_SCORE = 9_223_372_036_854_775_807


class RatingStatus(StrEnum):
    SKIPPED = 'SKIPPED'
    PENDING = 'PENDING'
    COMPLETED = 'COMPLETED'
    REVIEW = 'REVIEW'


def rating_message_id(run_id):
    return uuid5(run_id,'rating.run.xp')


def validate_rating_receipt(payload, receipt):
    if (not isinstance(receipt,dict) or any(receipt.get(k)!=v for k,v in payload.items())
            or type(receipt.get('points')) is not int or type(receipt.get('scoreAfter')) is not int
            or not payload['points'] <= receipt['scoreAfter'] <= MAX_SCORE):
        raise ValueError('Invalid rating acknowledgement')
    UUID(receipt['boardId'])
