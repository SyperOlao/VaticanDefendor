from enum import StrEnum


MAX_SCORE = 9_223_372_036_854_775_807
MESSAGE_CONSUMER = 'rewards.v1'


class RatingEvent(StrEnum):
    RUN = 'run.rating'


class ScoreSource(StrEnum):
    RUN_XP = 'RUN_XP'


class MessageStatus(StrEnum):
    APPLIED = 'APPLIED'
