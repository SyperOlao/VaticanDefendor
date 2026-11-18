from __future__ import annotations

import collections
import hashlib
import json
import math
import random
from pathlib import Path


Width = 15
CellSizeMeters = 2.4
ChurchPosition = (0.0, -3.0)
HeroCell = (7, 7)
AllCells = [(Horizontal, Vertical) for Horizontal in range(Width) for Vertical in range(Width)]


def Position(Cell):
    return ((Cell[0] - Width // 2) * CellSizeMeters, (Cell[1] - Width // 2) * CellSizeMeters)


def Neighbors(Cell):
    for Offset in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Candidate = (Cell[0] + Offset[0], Cell[1] + Offset[1])
        if 0 <= Candidate[0] < Width and 0 <= Candidate[1] < Width:
            yield Candidate


def Reachable(Blocked):
    Result = {HeroCell}
    Queue = collections.deque(Result)
    while Queue:
        for Candidate in Neighbors(Queue.popleft()):
            if Candidate not in Blocked and Candidate not in Result:
                Result.add(Candidate)
                Queue.append(Candidate)
    return Result


def SegmentDistance(Point, Start, End):
    Delta = (End[0] - Start[0], End[1] - Start[1])
    LengthSquared = Delta[0] ** 2 + Delta[1] ** 2
    Factor = ((Point[0] - Start[0]) * Delta[0] + (Point[1] - Start[1]) * Delta[1]) / LengthSquared
    Factor = max(0.0, min(1.0, Factor))
    return math.dist(Point, (Start[0] + Factor * Delta[0], Start[1] + Factor * Delta[1]))


def ProtectedCells(ApproachCount, PassageWidthMeters, ResourceSquares):
    Entries = [(0.0, 16.8), (0.0, -16.8), (-16.8, 0.0), (16.8, 0.0)]
    if ApproachCount == 2:
        Entries = Entries[:1] + Entries[3:]
    Deposits = [(-6.0, 0.0), (7.0, 5.0), (-7.0, 6.0), (6.0, -6.0)]
    Result = set()
    for Cell in AllCells:
        WorldPosition = Position(Cell)
        if math.dist(WorldPosition, ChurchPosition) <= 5.0:
            Result.add(Cell)
        if any(SegmentDistance(WorldPosition, ChurchPosition, Entry) <= PassageWidthMeters / 2 for Entry in Entries):
            Result.add(Cell)
        if ResourceSquares and any(abs(WorldPosition[0] - Deposit[0]) <= 3.6 and abs(WorldPosition[1] - Deposit[1]) <= 3.6 for Deposit in Deposits):
            Result.add(Cell)
    return Result


def CircleCells(Center, RadiusMeters):
    ExpandedRadiusMeters = RadiusMeters + CellSizeMeters * 0.35
    return [Cell for Cell in AllCells if math.dist(Position(Cell), Center) <= ExpandedRadiusMeters]


def BoxCells(Center, HalfExtentMeters):
    ExpandedHalfExtentMeters = HalfExtentMeters + CellSizeMeters * 0.35
    return [Cell for Cell in AllCells if abs(Position(Cell)[0] - Center[0]) <= ExpandedHalfExtentMeters and abs(Position(Cell)[1] - Center[1]) <= ExpandedHalfExtentMeters]


def Main():
    OccupancyCases = []
    TargetBlockedCells = round(len(AllCells) * 0.20)
    for ApproachCount in (2, 4):
        for PassageWidthMeters in (4.8, 7.2):
            for ResourceSquares in (False, True):
                Protected = ProtectedCells(ApproachCount, PassageWidthMeters, ResourceSquares)
                EligibleCount = len(AllCells) - len(Protected)
                OccupancyCases.append({
                    "ApproachCount": ApproachCount,
                    "PassageWidthMeters": PassageWidthMeters,
                    "ResourceSquares": ResourceSquares,
                    "ProtectedCells": len(Protected),
                    "ProtectedFraction": len(Protected) / len(AllCells),
                    "EligibleCells": EligibleCount,
                    "RequestedBlockedCells": TargetBlockedCells,
                    "EligibleBlockedFractionRequired": TargetBlockedCells / EligibleCount,
                })

    Protected = ProtectedCells(4, 7.2, True)
    Corners = {(0, 0), (0, 14), (14, 0), (14, 14)}
    CornerBlockers = {(1, 0), (0, 1), (1, 14), (0, 13), (13, 0), (14, 1), (13, 14), (14, 13)}
    Generator = random.Random(120112)
    Eligible = [Cell for Cell in AllCells if Cell not in Protected | Corners | CornerBlockers]
    Blocked = CornerBlockers | set(Generator.sample(Eligible, TargetBlockedCells - len(CornerBlockers)))
    ReachableCells = Reachable(Blocked)
    OpenCount = len(AllCells) - len(Blocked)

    RasterCases = []
    for Center in ((0.0, 0.0), (1.2, 0.0), (1.2, 1.2)):
        RasterCases.append({
            "CenterMeters": Center,
            "CircleRadiusMeters": 0.82868874,
            "CircleBlockedCells": len(CircleCells(Center, 0.82868874)),
            "BoxHalfExtentMeters": 0.8,
            "BoxBlockedCells": len(BoxCells(Center, 0.8)),
        })

    Result = {
        "Scope": "Planning-only counterexamples. Static Python grid calculations do not validate Unity gameplay or network prediction.",
        "Grid": {"Width": Width, "CellSizeMeters": CellSizeMeters, "ChurchPosition": ChurchPosition},
        "ReservationCases": OccupancyCases,
        "CornerCounterexample": {
            "BlockedCells": len(Blocked),
            "ProtectedCellsBlocked": len(Blocked & Protected),
            "UnreachableCorners": sorted(Corners - ReachableCells),
            "ReachableOpenFraction": len(ReachableCells) / OpenCount,
            "UnreachableOpenCells": OpenCount - len(ReachableCells),
            "BlockedCellsList": sorted(Blocked),
        },
        "RasterPhaseCases": RasterCases,
        "ProducerCandidateCounterexample": {
            "WoodCandidates": [11, 12], "StoneCandidates": [11, 12], "IronCandidates": [11, 12],
            "EveryResourceHasTwoCandidates": True,
            "MaximumSimultaneouslyPlacedProducers": 2,
        },
    }
    Output = Path(__file__).resolve().parents[3] / "Builds" / "MapPlanning" / "GeneratorCounterexamples"
    Output.mkdir(parents=True, exist_ok=True)
    (Output / "Counterexamples.json").write_text(json.dumps(Result, indent=2), encoding="utf-8")
    print(json.dumps(Result, indent=2))


if __name__ == "__main__":
    Main()
