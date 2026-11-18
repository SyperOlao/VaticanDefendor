"""Server-owned XP calculation and level thresholds; no persistence or HTTP dependencies."""
from dataclasses import dataclass
import hashlib
import json
import re
from enum import StrEnum
from uuid import UUID

MAX_XP = 9_223_372_036_854_775_807
MAX_COUNT = 2_147_483_647
MAX_TARGETS = 256
MAX_LEVELS = 10_000
class XPSource(StrEnum):
    HERO_KILL = "HERO_KILL"
    TOWER_KILL = "TOWER_KILL"
    BUILDING_UPGRADE = "BUILDING_UPGRADE"
    RUN_VICTORY = "RUN_VICTORY"


SOURCES = frozenset(XPSource)
TARGET = re.compile(r'[A-Za-z0-9_.:-]{1,128}')


def integer(value, low, high):
    return type(value) is int and low <= value <= high


@dataclass(frozen=True)
class RunStats:
    hero_kills: tuple[tuple[str, int], ...] = ()
    tower_kills: tuple[tuple[str, int], ...] = ()
    building_upgrades: tuple[tuple[str, int], ...] = ()

    def validate(self):
        for entries in (self.hero_kills, self.tower_kills, self.building_upgrades):
            if not isinstance(entries, tuple) or len(entries) > MAX_TARGETS:
                raise ValueError('Invalid XP counters')
            keys = set()
            for item in entries:
                if not isinstance(item, tuple) or len(item) != 2:
                    raise ValueError('Invalid XP counter')
                key, count = item
                if (not isinstance(key, str) or TARGET.fullmatch(key) is None
                        or key in keys or not integer(count, 0, MAX_COUNT)):
                    raise ValueError('Invalid XP counter')
                keys.add(key)

    def snapshot(self):
        self.validate()
        return {'heroKills': dict(sorted(self.hero_kills)), 'towerKills': dict(sorted(self.tower_kills)),
                'buildingUpgrades': dict(sorted(self.building_upgrades))}


@dataclass(frozen=True)
class XPRule:
    source_type: str
    target_key: str
    xp_per_unit: int
    max_xp_per_run: int

    def validate(self):
        if (not isinstance(self.source_type, str) or self.source_type not in SOURCES or not isinstance(self.target_key, str)
                or self.target_key != '*' and TARGET.fullmatch(self.target_key) is None
                or self.source_type == XPSource.RUN_VICTORY and self.target_key != '*'
                or not integer(self.xp_per_unit, 0, MAX_COUNT)
                or not integer(self.max_xp_per_run, 0, MAX_XP)):
            raise ValueError('Invalid XP rule')


@dataclass(frozen=True)
class LevelDefinition:
    level: int
    total_xp_required: int
    reward_bundle_id: UUID | None = None


@dataclass(frozen=True)
class ProgressionRules:
    ruleset_id: str
    rules: tuple[XPRule, ...]
    levels: tuple[LevelDefinition, ...]

    def __post_init__(self):
        if not isinstance(self.ruleset_id, str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}', self.ruleset_id) is None:
            raise ValueError('Invalid progression ruleset ID')
        if not isinstance(self.rules, tuple) or not 1 <= len(self.rules) <= 1024:
            raise ValueError('Provide explicit XP rules')
        seen = set()
        for rule in self.rules:
            rule.validate()
            key = (rule.source_type, rule.target_key)
            if key in seen:
                raise ValueError('Duplicate XP rule')
            seen.add(key)
        if sum(rule.max_xp_per_run for rule in self.rules) > MAX_XP:
            raise ValueError('XP caps exceed supported total')
        if not isinstance(self.levels, tuple) or not 1 <= len(self.levels) <= MAX_LEVELS:
            raise ValueError('Provide explicit level thresholds')
        previous = -1
        for index, level in enumerate(self.levels, 1):
            if (not integer(level.level, 1, MAX_LEVELS) or level.level != index
                    or not integer(level.total_xp_required, 0, MAX_XP) or level.total_xp_required <= previous
                    or level.reward_bundle_id is not None and not isinstance(level.reward_bundle_id, UUID)):
                raise ValueError('Level thresholds must be contiguous and strictly increasing')
            previous = level.total_xp_required
        if self.levels[0].total_xp_required != 0 or self.levels[0].reward_bundle_id is not None:
            raise ValueError('Level one starts at zero XP without a level-up gift')

    def snapshot(self):
        return {'schemaVersion': 1, 'rulesetId': self.ruleset_id,
                'rules': [{'sourceType': r.source_type, 'targetKey': r.target_key, 'xpPerUnit': r.xp_per_unit,
                           'maxXpPerRun': r.max_xp_per_run} for r in sorted(self.rules, key=lambda r: (r.source_type, r.target_key))],
                'levels': [{'level': d.level, 'totalXpRequired': d.total_xp_required,
                            'rewardBundleId': str(d.reward_bundle_id) if d.reward_bundle_id else None} for d in self.levels]}

    @classmethod
    def from_snapshot(cls, value):
        try:
            if set(value) != {'schemaVersion', 'rulesetId', 'rules', 'levels'} or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1:
                raise ValueError('Invalid progression snapshot')
            if not isinstance(value['rules'], list) or not isinstance(value['levels'], list):
                raise ValueError('Invalid progression snapshot')
            rules = []
            for r in value['rules']:
                if set(r) != {'sourceType', 'targetKey', 'xpPerUnit', 'maxXpPerRun'}:
                    raise ValueError('Invalid XP rule fields')
                rules.append(XPRule(r['sourceType'], r['targetKey'], r['xpPerUnit'], r['maxXpPerRun']))
            levels = []
            for d in value['levels']:
                if set(d) != {'level', 'totalXpRequired', 'rewardBundleId'}:
                    raise ValueError('Invalid level fields')
                levels.append(LevelDefinition(d['level'], d['totalXpRequired'], UUID(d['rewardBundleId']) if d['rewardBundleId'] is not None else None))
            return cls(value['rulesetId'], tuple(rules), tuple(levels))
        except (KeyError, TypeError, AttributeError):
            raise ValueError('Invalid progression configuration') from None

    def digest(self):
        return hashlib.sha256(json.dumps(self.snapshot(), sort_keys=True, separators=(',', ':')).encode()).digest()


@dataclass(frozen=True)
class XPBreakdown:
    hero_kill_xp: int
    tower_kill_xp: int
    tycoon_xp: int
    victory_xp: int

    @property
    def total_xp(self):
        return self.hero_kill_xp + self.tower_kill_xp + self.tycoon_xp + self.victory_xp

    def response(self):
        return {'heroKillXp': self.hero_kill_xp, 'towerKillXp': self.tower_kill_xp,
                'tycoonXp': self.tycoon_xp, 'victoryXp': self.victory_xp}


def calculate_xp(rules: ProgressionRules, stats: RunStats, victory: bool) -> XPBreakdown:
    stats.validate()
    index = {(r.source_type, r.target_key): r for r in rules.rules}
    counts = {}
    for source, entries in ((XPSource.HERO_KILL, stats.hero_kills), (XPSource.TOWER_KILL, stats.tower_kills),
                            (XPSource.BUILDING_UPGRADE, stats.building_upgrades)):
        for target, count in entries:
            key = (source, target) if (source, target) in index else (source, '*')
            if key in index:
                counts[key] = counts.get(key, 0) + count
    if victory and (XPSource.RUN_VICTORY, '*') in index:
        counts[(XPSource.RUN_VICTORY, '*')] = 1
    totals = dict.fromkeys(SOURCES, 0)
    for key, count in counts.items():
        rule = index[key]
        totals[rule.source_type] += min(rule.max_xp_per_run, rule.xp_per_unit * count)
    return XPBreakdown(totals[XPSource.HERO_KILL], totals[XPSource.TOWER_KILL], totals[XPSource.BUILDING_UPGRADE], totals[XPSource.RUN_VICTORY])


def advance_level(rules: ProgressionRules, total_xp: int, previous_level: int) -> int:
    if not integer(total_xp, 0, MAX_XP) or not integer(previous_level, 1, MAX_COUNT):
        raise ValueError('Invalid player progression')
    earned = max(d.level for d in rules.levels if d.total_xp_required <= total_xp)
    return max(previous_level, earned)
