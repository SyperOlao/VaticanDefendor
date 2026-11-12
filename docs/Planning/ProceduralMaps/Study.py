from __future__ import annotations

import argparse
import collections
import dataclasses
import hashlib
import json
import math
from pathlib import Path
import random
import statistics
import time

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as Plot
import numpy as Numpy


@dataclasses.dataclass(frozen=True)
class StudyConfiguration:
    Width: int = 15
    CellSize: float = 2.4
    BlockedFraction: float = 0.20
    ApproachCount: int = 4
    MinimumPassageCells: int = 3
    CoreRadiusMeters: float = 5.0
    ResourceInteractionMeters: float = 2.6
    ProductionRangeMeters: float = 5.0
    WalkingSpeed: float = 4.8


@dataclasses.dataclass
class CandidateMap:
    Configuration: StudyConfiguration
    Method: str
    Seed: int
    Blocked: Numpy.ndarray
    Protected: Numpy.ndarray
    Entries: list[tuple[int, int]]
    Deposits: list[tuple[int, int]]
    Hero: tuple[int, int]
    Church: tuple[int, int]


def Neighbors(Cell, Width):
    Horizontal, Vertical = Cell
    for OffsetHorizontal, OffsetVertical in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        Candidate = Horizontal + OffsetHorizontal, Vertical + OffsetVertical
        if 0 <= Candidate[0] < Width and 0 <= Candidate[1] < Width:
            yield Candidate


def Distances(Blocked, Starts):
    Width = len(Blocked)
    Result = Numpy.full((Width, Width), -1, dtype=Numpy.int16)
    Queue = collections.deque()
    for Cell in Starts:
        if not Blocked[Cell] and Result[Cell] < 0:
            Result[Cell] = 0
            Queue.append(Cell)
    while Queue:
        Current = Queue.popleft()
        for Candidate in Neighbors(Current, Width):
            if not Blocked[Candidate] and Result[Candidate] < 0:
                Result[Candidate] = Result[Current] + 1
                Queue.append(Candidate)
    return Result


def MarkCorridor(Protected, Start, End, Radius):
    DeltaHorizontal = End[0] - Start[0]
    DeltaVertical = End[1] - Start[1]
    Steps = max(abs(DeltaHorizontal), abs(DeltaVertical), 1)
    for Step in range(Steps + 1):
        Horizontal = round(Start[0] + DeltaHorizontal * Step / Steps)
        Vertical = round(Start[1] + DeltaVertical * Step / Steps)
        for OffsetHorizontal in range(-Radius, Radius + 1):
            for OffsetVertical in range(-Radius, Radius + 1):
                CandidateHorizontal = Horizontal + OffsetHorizontal
                CandidateVertical = Vertical + OffsetVertical
                if 0 <= CandidateHorizontal < len(Protected) and 0 <= CandidateVertical < len(Protected):
                    Protected[CandidateHorizontal, CandidateVertical] = True


def ExpandedBlockers(Blocked):
    Result = Blocked.copy()
    for Horizontal, Vertical in Numpy.argwhere(Blocked):
        Result[max(0, Horizontal - 1):Horizontal + 2, max(0, Vertical - 1):Vertical + 2] = True
    return Result


def SelectCells(Generator, Cells, Count):
    Cells = list(Cells)
    Generator.shuffle(Cells)
    return Cells[:Count]


def Generate(Method, Seed, Configuration):
    Generator = random.Random(Seed)
    Width = Configuration.Width
    Middle = Width // 2
    Hero = Middle, Middle
    Church = Middle, Middle - 1
    Protected = Numpy.zeros((Width, Width), dtype=bool)
    for Horizontal in range(Width):
        for Vertical in range(Width):
            if math.dist((Horizontal, Vertical), Church) * Configuration.CellSize <= Configuration.CoreRadiusMeters:
                Protected[Horizontal, Vertical] = True
    Rotation = Generator.uniform(-0.16, 0.16)
    Entries = []
    for Index in range(Configuration.ApproachCount):
        Angle = Rotation + Index * math.tau / Configuration.ApproachCount
        DirectionHorizontal, DirectionVertical = math.cos(Angle), math.sin(Angle)
        Scale = Middle / max(abs(DirectionHorizontal), abs(DirectionVertical))
        Entries.append((round(Middle + Scale * DirectionHorizontal), round(Middle + Scale * DirectionVertical)))
    for Entry in Entries:
        MarkCorridor(Protected, Entry, Entry, 1)
    Deposits = []
    ResourceBands = ((6, 8), (8, 12), (7, 10), (9, 13))
    for Lower, Upper in ResourceBands:
        Candidates = [(Horizontal, Vertical) for Horizontal in range(1, Width - 1)
                      for Vertical in range(1, Width - 1)
                      if Lower <= math.dist((Horizontal, Vertical), Church) * Configuration.CellSize <= Upper
                      and all(math.dist((Horizontal, Vertical), Existing) * Configuration.CellSize >= 3.6 for Existing in Deposits)]
        if not Candidates:
            raise ValueError("Resource bands do not fit the configured arena.")
        Deposits.append(Generator.choice(Candidates))
    for Deposit in Deposits:
        Protected[Deposit] = True
    if Method in ("ProtectedSectors", "ConnectedGraph"):
        Radius = Configuration.MinimumPassageCells // 2
        for Entry in Entries:
            MarkCorridor(Protected, Church, Entry, Radius)
        for Deposit in Deposits:
            MarkCorridor(Protected, Hero, Deposit, 1)
            MarkCorridor(Protected, Deposit, Deposit, 1)
        if Method == "ConnectedGraph":
            Inner = [(round((Entry[0] + Middle) * 0.5), round((Entry[1] + Middle) * 0.5)) for Entry in Entries]
            for Index, Point in enumerate(Inner):
                MarkCorridor(Protected, Point, Inner[(Index + 1) % len(Inner)], Radius)
    Eligible = [tuple(map(int, Cell)) for Cell in Numpy.argwhere(~Protected)]
    Count = min(round(Width * Width * Configuration.BlockedFraction), len(Eligible))
    Blocked = Numpy.zeros_like(Protected)
    if Method == "ClusteredNoise":
        Field = Numpy.array([[Generator.random() for Vertical in range(Width)] for Horizontal in range(Width)])
        for Pass in range(3):
            Field = (Field + Numpy.roll(Field, 1, 0) + Numpy.roll(Field, -1, 0) +
                     Numpy.roll(Field, 1, 1) + Numpy.roll(Field, -1, 1)) / 5
        Selected = sorted(Eligible, key=lambda Cell: Field[Cell], reverse=True)[:Count]
    else:
        Selected = SelectCells(Generator, Eligible, Count)
    for Cell in Selected:
        Blocked[Cell] = True
    Blocked[Church] = True
    return CandidateMap(Configuration, Method, Seed, Blocked, Protected, Entries, Deposits, Hero, Church)


def Measure(Candidate):
    Configuration = Candidate.Configuration
    Width = Configuration.Width
    HomeDistances = Distances(Candidate.Blocked, [Candidate.Hero])
    ChurchInteraction = [(Horizontal, Vertical) for Horizontal in range(Width) for Vertical in range(Width)
                         if math.dist((Horizontal, Vertical), Candidate.Church) * Configuration.CellSize <= 3.7]
    ThreatDistances = Distances(Candidate.Blocked, ChurchInteraction)
    Expanded = ExpandedBlockers(Candidate.Blocked)
    CourtyardApproaches = [(Horizontal, Vertical) for Horizontal in range(Width) for Vertical in range(Width)
                          if math.dist((Horizontal, Vertical), Candidate.Church) * Configuration.CellSize <= Configuration.CoreRadiusMeters]
    WiderDistances = Distances(Expanded, CourtyardApproaches)
    EntryLengths = [int(ThreatDistances[Entry]) * Configuration.CellSize for Entry in Candidate.Entries
                    if ThreatDistances[Entry] >= 0]
    Trips = []
    ProducerCounts = []
    ResourceReachable = []
    for Deposit in Candidate.Deposits:
        ApproachDistances = [int(HomeDistances[Horizontal, Vertical]) * Configuration.CellSize
                             for Horizontal in range(Width) for Vertical in range(Width)
                             if HomeDistances[Horizontal, Vertical] >= 0
                             and math.dist((Horizontal, Vertical), Deposit) * Configuration.CellSize <= Configuration.ResourceInteractionMeters]
        ResourceReachable.append(bool(ApproachDistances))
        Trip = None
        if ApproachDistances:
            Trip = 2 * min(ApproachDistances) / Configuration.WalkingSpeed + 4 * 0.7
        Trips.append(Trip)
        ProducerCounts.append(sum(1 for Horizontal in range(1, Width - 1) for Vertical in range(1, Width - 1)
                                  if HomeDistances[Horizontal, Vertical] >= 0
                                  and 1.4 < math.dist((Horizontal, Vertical), Deposit) * Configuration.CellSize <= Configuration.ProductionRangeMeters
                                  and all(not Candidate.Blocked[Neighbor] for Neighbor in Neighbors((Horizontal, Vertical), Width))
                                  and all(math.dist((Horizontal, Vertical), Other) * Configuration.CellSize >= 1.4 for Other in Candidate.Deposits)
                                  and math.dist((Horizontal, Vertical), Candidate.Church) * Configuration.CellSize > 2.3))
    OpenCells = int((~Candidate.Blocked).sum())
    ReachableFraction = int((HomeDistances >= 0).sum()) / max(OpenCells, 1)
    DisconnectedEntries = len(Candidate.Entries) - len(EntryLengths)
    WideEntries = sum(WiderDistances[Entry] >= 0 for Entry in Candidate.Entries)
    HasProduction = all(Count > 0 for Count in ProducerCounts)
    StructuralPass = DisconnectedEntries == 0 and all(ResourceReachable) and HasProduction and ReachableFraction >= 0.98
    BroadPass = StructuralPass and WideEntries == len(Candidate.Entries)
    return {
        "Method": Candidate.Method, "Seed": Candidate.Seed, "Width": Width,
        "RequestedBlockedFraction": Configuration.BlockedFraction,
        "ActualBlockedFraction": float(Candidate.Blocked.mean()),
        "ReachableOpenFraction": ReachableFraction, "DisconnectedEntries": DisconnectedEntries,
        "WideEntries": int(WideEntries), "ResourceReachable": ResourceReachable,
        "ProducerCounts": ProducerCounts, "ShortGatherReturnSeconds16": Trips,
        "EntryMinimumMeters": min(EntryLengths, default=None),
        "EntryMaximumMeters": max(EntryLengths, default=None),
        "StructuralPass": bool(StructuralPass), "BroadPass": bool(BroadPass),
        "MapHash": hashlib.sha256(Candidate.Blocked.tobytes()).hexdigest(),
    }


def Summarize(Measurements):
    Groups = collections.defaultdict(list)
    for Measurement in Measurements:
        Groups[(Measurement["Method"], Measurement["Width"], Measurement["RequestedBlockedFraction"])].append(Measurement)
    Summary = []
    for (Method, Width, Fraction), Entries in Groups.items():
        WoodTrips = [Entry["ShortGatherReturnSeconds16"][0] for Entry in Entries
                     if Entry["ShortGatherReturnSeconds16"][0] is not None]
        Summary.append({
            "Method": Method, "Width": Width, "BlockedFraction": Fraction, "Count": len(Entries),
            "StructuralPassPercent": round(100 * sum(Entry["StructuralPass"] for Entry in Entries) / len(Entries), 2),
            "BroadPassPercent": round(100 * sum(Entry["BroadPass"] for Entry in Entries) / len(Entries), 2),
            "MeanActualBlockedFraction": round(statistics.mean(Entry["ActualBlockedFraction"] for Entry in Entries), 4),
            "WoodTrip16MedianSeconds": round(statistics.median(WoodTrips), 2),
            "WoodTrip16MaximumSeconds": round(max(WoodTrips), 2),
            "UniqueMasks": len({Entry["MapHash"] for Entry in Entries}),
        })
    return Summary


def DrawExamples(Output):
    Methods = ["RandomScatter", "ClusteredNoise", "ProtectedSectors", "ConnectedGraph"]
    Figure, Axes = Plot.subplots(2, 4, figsize=(16, 9), constrained_layout=True)
    for Row, Seed in enumerate([17, 483]):
        for Column, Method in enumerate(Methods):
            Candidate = Generate(Method, Seed, StudyConfiguration())
            Measurement = Measure(Candidate)
            Axis = Axes[Row, Column]
            Axis.set_facecolor("#6c805e")
            for Horizontal, Vertical in Numpy.argwhere(Candidate.Protected):
                Axis.add_patch(Plot.Rectangle((Horizontal - 0.5, Vertical - 0.5), 1, 1, color="#81977a", alpha=0.75))
            for Horizontal, Vertical in Numpy.argwhere(Candidate.Blocked):
                Axis.add_patch(Plot.Rectangle((Horizontal - 0.48, Vertical - 0.48), 0.96, 0.96, color="#304a37"))
            Axis.scatter(*Candidate.Church, marker="P", s=130, color="#bf6a52", edgecolor="#fff7dc", zorder=5)
            Axis.scatter(*Candidate.Hero, marker="o", s=45, color="#6084c2", edgecolor="white", zorder=6)
            for Index, Deposit in enumerate(Candidate.Deposits):
                Axis.scatter(*Deposit, marker="s", s=70, color=["#d5b17a", "#d5b17a", "#b6bdc6", "#718496"][Index], edgecolor="black", zorder=5)
                Axis.text(Deposit[0], Deposit[1] + 0.45, ["Wood 1", "Wood 2", "Stone", "Iron"][Index], fontsize=6, ha="center")
            for Entry in Candidate.Entries:
                Axis.scatter(*Entry, marker="v", color="#a82e29", s=60, zorder=5)
            Axis.set(xlim=(-0.5, 14.5), ylim=(-0.5, 14.5), aspect="equal")
            Axis.set_xticks(range(15), minor=True)
            Axis.set_yticks(range(15), minor=True)
            Axis.grid(which="minor", alpha=0.15)
            Axis.set_xticks([])
            Axis.set_yticks([])
            Axis.set_title(f"{Method}, seed {Seed}\nstructural={Measurement['StructuralPass']}, broad={Measurement['BroadPass']}", fontsize=9)
    Figure.suptitle("Offline layout study. Cells 2.4 m; not Unity gameplay validation.", fontsize=14)
    Figure.savefig(Output / "LayoutComparison.svg")
    Figure.savefig(Output / "LayoutComparison.png", dpi=140)
    Plot.close(Figure)


def Main():
    Parser = argparse.ArgumentParser()
    Parser.add_argument("--seeds", type=int, default=1000)
    Parser.add_argument("--output", type=Path, default=Path(__file__).parent / "Results")
    Arguments = Parser.parse_args()
    Arguments.output.mkdir(parents=True, exist_ok=True)
    Started = time.perf_counter()
    Measurements = []
    for Fraction in [0.12, 0.20, 0.28]:
        for Method in ["RandomScatter", "ClusteredNoise", "ProtectedSectors", "ConnectedGraph"]:
            for Seed in range(Arguments.seeds):
                Candidate = Generate(Method, Seed, StudyConfiguration(BlockedFraction=Fraction))
                Measurements.append(Measure(Candidate))
            print(f"Completed {Method} density={Fraction}: {Arguments.seeds} seeds", flush=True)
    Summary = Summarize(Measurements)
    (Arguments.output / "Summary.json").write_text(json.dumps(Summary, indent=2), encoding="utf-8")
    (Arguments.output / "Measurements.json").write_text(json.dumps(Measurements, indent=2), encoding="utf-8")
    DrawExamples(Arguments.output)
    print(json.dumps({"Candidates": len(Measurements), "Seconds": round(time.perf_counter() - Started, 2)}), flush=True)


if __name__ == "__main__":
    Main()
