from pathlib import Path
import argparse
import heapq
import json
import math
import time

import numpy as Numpy
from scipy.ndimage import binary_erosion, distance_transform_edt, maximum_filter, minimum_filter
from scipy.spatial import Delaunay


ExperimentDirectory = Path(__file__).resolve().parent
OutputDirectory = ExperimentDirectory / 'Results'
HalfExtent = 24.0
SampleCount = 129
Spacing = 2 * HalfExtent / (SampleCount - 1)
Coordinates = Numpy.linspace(-HalfExtent, HalfExtent, SampleCount)
Horizontal, Vertical = Numpy.meshgrid(Coordinates, Coordinates)
ChurchPosition = (0.0, -2.0)
HorizontalScale = 1.0
Profiles = [
    {'Id': 'meadow', 'Name': 'Холмистая поляна', 'Description': 'Пологие гряды и открытый центр. Контрольный вариант без жидкости.', 'Liquid': 'none'},
    {'Id': 'river', 'Name': 'Река с двумя бродами', 'Description': 'Извилистое русло делит карту. Два мелких брода связывают берега.', 'Liquid': 'water'},
    {'Id': 'lake', 'Name': 'Озёрный берег', 'Description': 'Глубокая вода закрывает восточный подход. Строительство смещается на берег.', 'Liquid': 'water'},
    {'Id': 'wetland', 'Name': 'Затопленные низины', 'Description': 'Несколько водоёмов разделены сухими перемычками. Проверяется потеря путей при подъёме воды.', 'Liquid': 'water'},
    {'Id': 'caldera', 'Name': 'Вулканическая чаша', 'Description': 'Каменный обод и лавовое озеро. Лава непроходима, а подходы проходят вокруг чаши.', 'Liquid': 'lava'},
]


def Gaussian(HorizontalPosition, VerticalPosition, CenterHorizontal, CenterVertical, WidthHorizontal, WidthVertical):
    return Numpy.exp(-((HorizontalPosition - CenterHorizontal) / WidthHorizontal) ** 2 - ((VerticalPosition - CenterVertical) / WidthVertical) ** 2)


def Surface(Profile, Seed, HorizontalPosition, VerticalPosition):
    HorizontalPosition = HorizontalPosition / HorizontalScale
    VerticalPosition = VerticalPosition / HorizontalScale
    Random = Numpy.random.default_rng(Seed)
    Phase = Random.uniform(-math.pi, math.pi, 4)
    Elevation = 1.25 + 0.48 * Numpy.sin(HorizontalPosition * 0.17 + Phase[0]) * Numpy.cos(VerticalPosition * 0.13 + Phase[1])
    Elevation += 0.28 * Numpy.sin(HorizontalPosition * 0.32 + VerticalPosition * 0.21 + Phase[2])
    Elevation += 0.10 * Numpy.cos(HorizontalPosition * 0.67 - VerticalPosition * 0.44 + Phase[3])
    Elevation += 2.1 * Gaussian(HorizontalPosition, VerticalPosition, -14, 12, 9, 8)
    Elevation += 1.4 * Gaussian(HorizontalPosition, VerticalPosition, 16, -13, 8, 10)
    if Profile['Id'] == 'meadow':
        Elevation += 2.5 * Gaussian(HorizontalPosition, VerticalPosition, 7, 15, 11, 7)
        Elevation -= 0.5 * Gaussian(HorizontalPosition, VerticalPosition, -9, -12, 8, 9)
    if Profile['Id'] == 'river':
        RiverCenter = 6 + 2.8 * Numpy.sin(VerticalPosition * 0.16 + Phase[0] * 0.25)
        Channel = Numpy.exp(-((HorizontalPosition - RiverCenter) / 3.0) ** 2)
        Ford = Numpy.maximum(Numpy.exp(-((VerticalPosition + 10) / 2.4) ** 4), Numpy.exp(-((VerticalPosition - 10) / 2.4) ** 4))
        RiverBottom = -1.35 + 1.28 * Ford
        Elevation = Elevation * (1 - Channel) + RiverBottom * Channel
    if Profile['Id'] == 'lake':
        CenterHorizontal = 12 + 0.8 * math.sin(Phase[0])
        CenterVertical = 5 + 1.0 * math.sin(Phase[1])
        Basin = Gaussian(HorizontalPosition, VerticalPosition, CenterHorizontal, CenterVertical, 11.5, 12)
        Elevation -= 4.5 * Basin
    if Profile['Id'] == 'wetland':
        for CenterHorizontal, CenterVertical, BasinWidth in [(-12, 8, 6.8), (11, 10, 7.6), (12, -12, 7.0), (-12, -13, 6.0)]:
            Elevation -= 2.7 * Gaussian(HorizontalPosition, VerticalPosition, CenterHorizontal + math.sin(Phase[0]), CenterVertical + math.cos(Phase[1]), BasinWidth, BasinWidth * 0.82)
    if Profile['Id'] == 'caldera':
        Radius = Numpy.sqrt(((HorizontalPosition - 10) / 1.12) ** 2 + ((VerticalPosition - 8) / 0.9) ** 2)
        Elevation += 2.6 * Numpy.exp(-((Radius - 9.5) / 3.1) ** 2)
        Elevation -= 4.3 * Numpy.exp(-(Radius / 7.0) ** 4)
        Elevation -= 1.7 * Gaussian(HorizontalPosition, VerticalPosition, 2, 14, 3.8, 6)
    Clearing = Gaussian(HorizontalPosition, VerticalPosition, ChurchPosition[0] / HorizontalScale, ChurchPosition[1] / HorizontalScale, 5.5, 5.5)
    Elevation = Elevation * (1 - Clearing) + 1.3 * Clearing
    return Elevation


def Boundary(Seed, HorizontalPosition, VerticalPosition):
    Angle = Numpy.arctan2(VerticalPosition, HorizontalPosition)
    Radius = (22.1 + 1.0 * Numpy.sin(Angle * 3 + Seed * 0.19) + 0.65 * Numpy.cos(Angle * 5 - Seed * 0.11)) * HorizontalScale
    return Numpy.hypot(HorizontalPosition, VerticalPosition) < Radius


def Cell(Position):
    return (int(round((Position[1] + HalfExtent) / Spacing)), int(round((Position[0] + HalfExtent) / Spacing)))


def WorldPosition(CellPosition, Elevation):
    Row, Column = CellPosition
    return [round(float(Coordinates[Column]), 3), round(float(Elevation[Row, Column]), 3), round(float(Coordinates[Row]), 3)]


def PlaceObjects(Profile, Seed, Elevation, Slope, Inside):
    Random = Numpy.random.default_rng(Seed + 4000)
    Objects = []
    ReservedPositions = [(Position[0] * HorizontalScale, Position[1] * HorizontalScale) for Position in [(0, -2), (-5, -3), (-7, 7), (7, -8), (10, 8)]]
    for ObjectIndex in range(round(220 * HorizontalScale ** 2)):
        HorizontalPosition, VerticalPosition = Random.uniform(-21 * HorizontalScale, 21 * HorizontalScale, 2)
        Row, Column = Cell((HorizontalPosition, VerticalPosition))
        if not Inside[Row, Column] or Elevation[Row, Column] < 0.65 or Slope[Row, Column] > 24:
            continue
        if math.dist((HorizontalPosition, VerticalPosition), ChurchPosition) < 7 * HorizontalScale:
            continue
        if min(math.dist((HorizontalPosition, VerticalPosition), Reserved) for Reserved in ReservedPositions) < 2.6:
            continue
        Cluster = math.sin(HorizontalPosition / HorizontalScale * 0.24 + Seed) * math.cos(VerticalPosition / HorizontalScale * 0.21 - Seed)
        if Cluster < -0.15:
            continue
        Kind = 'tree'
        if Profile['Id'] == 'caldera' or Random.random() < 0.22:
            Kind = 'rock'
        Radius = float(Random.uniform(0.38, 0.7))
        if any(math.hypot(HorizontalPosition - Existing['Position'][0], VerticalPosition - Existing['Position'][2]) < Radius + Existing['Radius'] + 0.4 for Existing in Objects):
            continue
        Objects.append({'Kind': Kind, 'Position': [round(float(HorizontalPosition), 3), round(float(Surface(Profile, Seed, HorizontalPosition, VerticalPosition)), 3), round(float(VerticalPosition), 3)], 'Radius': round(Radius, 3), 'Scale': round(float(Random.uniform(0.8, 1.4)), 3), 'Rotation': round(float(Random.uniform(0, math.tau)), 3)})
        if len(Objects) >= round(44 * HorizontalScale ** 2):
            break
    return Objects


def MovementMask(Profile, Seed, Elevation, Slope, Inside, Objects, LiquidLevel):
    Passable = Inside & (Slope <= 28)
    if Profile['Liquid'] == 'water':
        Passable &= Elevation >= LiquidLevel - 0.28
    if Profile['Liquid'] == 'lava':
        Passable &= Elevation >= LiquidLevel + 0.25
    Obstructed = Numpy.zeros_like(Passable)
    for Object in Objects:
        Obstructed |= Numpy.hypot(Horizontal - Object['Position'][0], Vertical - Object['Position'][2]) < Object['Radius']
    Passable &= ~Obstructed
    Clearance = distance_transform_edt(Passable) * Spacing
    return Passable & (Clearance >= 0.55)


def RouteField(Passable, Elevation, Goal):
    Distance = Numpy.full(Elevation.shape, Numpy.inf)
    Parent = Numpy.full((*Elevation.shape, 2), -1, dtype=Numpy.int16)
    if not Passable[Goal]:
        return Distance, Parent
    Distance[Goal] = 0
    Queue = [(0.0, Goal[0], Goal[1])]
    Directions = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]
    while Queue:
        CurrentDistance, Row, Column = heapq.heappop(Queue)
        if CurrentDistance > Distance[Row, Column]:
            continue
        for RowOffset, ColumnOffset in Directions:
            NextRow = Row + RowOffset
            NextColumn = Column + ColumnOffset
            if NextRow < 0 or NextColumn < 0 or NextRow >= SampleCount or NextColumn >= SampleCount:
                continue
            if not Passable[NextRow, NextColumn]:
                continue
            if RowOffset != 0 and ColumnOffset != 0:
                if not Passable[Row, NextColumn] or not Passable[NextRow, Column]:
                    continue
            HorizontalDistance = Spacing * math.hypot(RowOffset, ColumnOffset)
            HeightDifference = float(Elevation[NextRow, NextColumn] - Elevation[Row, Column])
            if abs(HeightDifference) > HorizontalDistance * math.tan(math.radians(28)):
                continue
            NextDistance = CurrentDistance + math.hypot(HorizontalDistance, HeightDifference)
            if NextDistance < Distance[NextRow, NextColumn]:
                Distance[NextRow, NextColumn] = NextDistance
                Parent[NextRow, NextColumn] = (Row, Column)
                heapq.heappush(Queue, (NextDistance, NextRow, NextColumn))
    return Distance, Parent


def TraceRoute(Start, Parent, Distance, Elevation):
    if not math.isfinite(float(Distance[Start])):
        return []
    Points = []
    Current = Start
    while Parent[Current][0] >= 0:
        Points.append(WorldPosition(Current, Elevation))
        Current = tuple(Parent[Current])
    Points.append(WorldPosition(Current, Elevation))
    return Points[::3] + [Points[-1]]


def Measure(Profile, Seed, Elevation, Slope, Inside, Objects, LiquidLevel, Baseline=None):
    Passable = MovementMask(Profile, Seed, Elevation, Slope, Inside, Objects, LiquidLevel)
    Distance, Parent = RouteField(Passable, Elevation, Cell(ChurchPosition))
    Reachable = Numpy.isfinite(Distance)
    Starts = []
    Routes = []
    EntryAngles = [math.pi * 0.18, math.pi * 0.69, math.pi * 1.18, math.pi * 1.72]
    if Profile['Id'] in ['lake', 'caldera']:
        EntryAngles = [1.15, 2.3, 3.8, 5.3]
    for EntryIndex, Angle in enumerate(EntryAngles):
        Nominal = (19 * HorizontalScale * math.cos(Angle), 19 * HorizontalScale * math.sin(Angle))
        Neighborhood = Numpy.hypot(Horizontal - Nominal[0], Vertical - Nominal[1])
        Candidates = Passable & (Neighborhood <= 3.5 * HorizontalScale)
        if Baseline is not None:
            BaselineEntry = Baseline['Entries'][EntryIndex]['Position']
            Candidates[:] = False
            if BaselineEntry is not None:
                BaselineCell = Cell((BaselineEntry[0], BaselineEntry[2]))
                if Passable[BaselineCell]:
                    Candidates[BaselineCell] = True
        if not Candidates.any():
            Starts.append({'Nominal': list(Nominal), 'Position': None, 'Distance': None})
            Routes.append([])
            continue
        CandidateDistance = Numpy.where(Candidates, Neighborhood, Numpy.inf)
        Start = Numpy.unravel_index(Numpy.argmin(CandidateDistance), Elevation.shape)
        RouteLength = None
        if math.isfinite(float(Distance[Start])):
            RouteLength = round(float(Distance[Start]), 2)
        Starts.append({'Nominal': list(Nominal), 'Position': WorldPosition(Start, Elevation), 'Distance': RouteLength})
        Routes.append(TraceRoute(Start, Parent, Distance, Elevation))
    FoundationRadius = 1.4
    FootprintCoordinates = Numpy.arange(-4, 5) * Spacing
    FootprintHorizontal, FootprintVertical = Numpy.meshgrid(FootprintCoordinates, FootprintCoordinates)
    Footprint = Numpy.hypot(FootprintHorizontal, FootprintVertical) <= FoundationRadius
    HeightSpread = maximum_filter(Elevation, footprint=Footprint) - minimum_filter(Elevation, footprint=Footprint)
    Buildable = binary_erosion(Passable & (Slope <= 10), structure=Footprint) & (HeightSpread <= 0.45)
    Buildable &= Numpy.hypot(Horizontal - ChurchPosition[0], Vertical - ChurchPosition[1]) > 3
    if Profile['Liquid'] != 'none':
        Buildable &= minimum_filter(Elevation, footprint=Footprint) >= LiquidLevel + 0.2
    Resources = []
    ResourceNominals = [('wood', (-5, -3)), ('wood', (-7, 7)), ('stone', (7, -8)), ('iron', (10, 8))]
    if Profile['Id'] in ['lake', 'caldera']:
        ResourceNominals[3] = ('iron', (-8, 13))
    if Profile['Id'] == 'wetland':
        ResourceNominals[3] = ('iron', (4, 12))
    ResourceNominals = [(Kind, (Position[0] * HorizontalScale, Position[1] * HorizontalScale)) for Kind, Position in ResourceNominals]
    for ResourceIndex, (ResourceKind, Nominal) in enumerate(ResourceNominals):
        Neighborhood = Numpy.hypot(Horizontal - Nominal[0], Vertical - Nominal[1])
        Candidates = Buildable & Reachable & (Neighborhood <= 7 * HorizontalScale)
        if Baseline is not None:
            BaselineResource = Baseline['Resources'][ResourceIndex]['Position']
            Candidates[:] = False
            if BaselineResource is not None:
                BaselineCell = Cell((BaselineResource[0], BaselineResource[2]))
                if Passable[BaselineCell] and Reachable[BaselineCell] and (Profile['Liquid'] == 'none' or Elevation[BaselineCell] >= LiquidLevel + 0.2):
                    Candidates[BaselineCell] = True
        if not Candidates.any():
            Resources.append({'Kind': ResourceKind, 'Position': None, 'Distance': None})
            continue
        CandidateDistance = Numpy.where(Candidates, Neighborhood, Numpy.inf)
        ResourceCell = Numpy.unravel_index(Numpy.argmin(CandidateDistance), Elevation.shape)
        Resources.append({'Kind': ResourceKind, 'Position': WorldPosition(ResourceCell, Elevation), 'Distance': round(float(Distance[ResourceCell]), 2)})
    LiquidFraction = 0.0
    if Profile['Liquid'] != 'none':
        LiquidFraction = float(((Elevation < LiquidLevel) & Inside).sum() / Inside.sum())
    ReachableFraction = float((Reachable & Passable).sum() / max(1, Passable.sum()))
    BuildArea = float((Buildable & Reachable).sum() * Spacing ** 2)
    BuildPoints = []
    for Row in range(0, SampleCount, 6):
        for Column in range(0, SampleCount, 6):
            if Buildable[Row, Column] and Reachable[Row, Column]:
                BuildPoints.append(WorldPosition((Row, Column), Elevation))
    ReachedCount = sum(Start['Distance'] is not None for Start in Starts)
    ResourceCount = sum(Resource['Position'] is not None for Resource in Resources)
    Accepted = ReachedCount == 4 and ResourceCount == 4 and BuildArea >= 140 and ReachableFraction >= 0.95
    return {'Level': LiquidLevel, 'ReachedEntries': ReachedCount, 'ReachableResources': ResourceCount, 'BuildableSquareMeters': round(BuildArea, 1), 'ReachableFraction': round(ReachableFraction, 4), 'LiquidFraction': round(LiquidFraction, 4), 'AcceptedGeometry': Accepted, 'Entries': Starts, 'Routes': Routes, 'Resources': Resources, 'BuildPoints': BuildPoints}


def ExportObj(FilePath, Points, Faces):
    with FilePath.open('w', encoding='utf-8') as Output:
        Output.write('o GeneratedTerrain\n')
        for Position in Points:
            Output.write(f'v {Position[0]:.4f} {Position[1]:.4f} {Position[2]:.4f}\n')
        for Face in Faces:
            Output.write(f'f {Face[0] + 1} {Face[1] + 1} {Face[2] + 1}\n')


def CreatePreview(Profile, Seed, Elevation, Objects, Measurements):
    Random = Numpy.random.default_rng(Seed + 20000)
    MeshCount = round(32 * HorizontalScale) + 1
    MeshCoordinates = Numpy.linspace(-HalfExtent, HalfExtent, MeshCount)
    MeshHorizontal, MeshVertical = Numpy.meshgrid(MeshCoordinates, MeshCoordinates)
    MeshHorizontal[1:-1, 1:-1] += Random.uniform(-0.2, 0.2, (MeshCount - 2, MeshCount - 2))
    MeshVertical[1:-1, 1:-1] += Random.uniform(-0.2, 0.2, (MeshCount - 2, MeshCount - 2))
    MeshInside = Boundary(Seed, MeshHorizontal, MeshVertical)
    InteriorHorizontal = MeshHorizontal[MeshInside]
    InteriorVertical = MeshVertical[MeshInside]
    Angles = Numpy.linspace(0, math.tau, round(120 * HorizontalScale), endpoint=False)
    Radius = (22.1 + Numpy.sin(Angles * 3 + Seed * 0.19) + 0.65 * Numpy.cos(Angles * 5 - Seed * 0.11)) * HorizontalScale
    MeshHorizontal = Numpy.concatenate([InteriorHorizontal, Radius * Numpy.cos(Angles)])
    MeshVertical = Numpy.concatenate([InteriorVertical, Radius * Numpy.sin(Angles)])
    MeshElevation = Surface(Profile, Seed, MeshHorizontal, MeshVertical)
    Points = Numpy.stack([MeshHorizontal, MeshElevation, MeshVertical], axis=-1)
    Triangulation = Delaunay(Points[:, [0, 2]])
    Faces = []
    for Triangle in Triangulation.simplices:
        Face = (int(Triangle[0]), int(Triangle[2]), int(Triangle[1]))
        Centroid = Points[list(Face)].mean(axis=0)
        if Boundary(Seed, Centroid[0], Centroid[2]):
            Faces.append(Face)
    MeshPath = OutputDirectory / f"{Profile['Id']}-{Seed}.obj"
    ExportObj(MeshPath, Points, Faces)
    Preview = {'Id': Profile['Id'], 'Name': Profile['Name'], 'Description': Profile['Description'], 'Liquid': Profile['Liquid'], 'Seed': Seed, 'AreaWidthMeters': HalfExtent * 2, 'Vertices': Numpy.round(Points, 3).tolist(), 'Triangles': Numpy.array(Faces).ravel().tolist(), 'Objects': Objects, 'Church': [ChurchPosition[0], round(float(Surface(Profile, Seed, ChurchPosition[0], ChurchPosition[1])), 3), ChurchPosition[1]], 'Measurements': Measurements}
    (OutputDirectory / f"{Profile['Id']}-{Seed}.json").write_text(json.dumps(Preview, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    return Preview


def Main():
    global HalfExtent, SampleCount, Spacing, Coordinates, Horizontal, Vertical, ChurchPosition, HorizontalScale, OutputDirectory
    Parser = argparse.ArgumentParser()
    Parser.add_argument('--width', type=float, choices=[48, 96], default=48)
    Parser.add_argument('--profile', choices=[Profile['Id'] for Profile in Profiles])
    Parser.add_argument('--output', type=Path)
    Arguments = Parser.parse_args()
    HorizontalScale = Arguments.width / 48
    HalfExtent = Arguments.width / 2
    SampleCount = round(Arguments.width / 0.375) + 1
    Spacing = Arguments.width / (SampleCount - 1)
    Coordinates = Numpy.linspace(-HalfExtent, HalfExtent, SampleCount)
    Horizontal, Vertical = Numpy.meshgrid(Coordinates, Coordinates)
    ChurchPosition = (0.0, -2.0 * HorizontalScale)
    SelectedProfiles = Profiles
    if Arguments.profile is not None:
        SelectedProfiles = [Profile for Profile in Profiles if Profile['Id'] == Arguments.profile]
    if Arguments.output is not None:
        OutputDirectory = Arguments.output.resolve()
    Started = time.perf_counter()
    OutputDirectory.mkdir(parents=True, exist_ok=True)
    Rows = []
    Previews = []
    for Profile in SelectedProfiles:
        AcceptedSeeds = []
        for Seed in range(100, 112):
            Elevation = Surface(Profile, Seed, Horizontal, Vertical)
            VerticalGradient, HorizontalGradient = Numpy.gradient(Elevation, Spacing)
            Slope = Numpy.degrees(Numpy.arctan(Numpy.hypot(HorizontalGradient, VerticalGradient)))
            Inside = Boundary(Seed, Horizontal, Vertical)
            Objects = PlaceObjects(Profile, Seed, Elevation, Slope, Inside)
            Normal = Measure(Profile, Seed, Elevation, Slope, Inside, Objects, 0.0)
            Raised = Measure(Profile, Seed, Elevation, Slope, Inside, Objects, 0.6, Normal)
            Measurements = [Normal, Raised]
            Rows.append({'Profile': Profile['Id'], 'Seed': Seed, 'ReliefMeters': round(float(Elevation[Inside].max() - Elevation[Inside].min()), 2), 'Normal': {Key: Value for Key, Value in Measurements[0].items() if Key not in ['Routes', 'BuildPoints']}, 'Raised': {Key: Value for Key, Value in Measurements[1].items() if Key not in ['Routes', 'BuildPoints']}})
            if Measurements[0]['AcceptedGeometry']:
                AcceptedSeeds.append(Seed)
            if Seed in [100, 105, 111]:
                Previews.append(CreatePreview(Profile, Seed, Elevation, Objects, Measurements))
        print(f"{Profile['Id']}: {len(AcceptedSeeds)}/12 accepted geometry; seeds={AcceptedSeeds}", flush=True)
    Summary = {'Scope': 'Offline geometric experiment. No gameplay, Netcode, combat, placement transactions, fluid dynamics or device performance verified.', 'AreaWidthMeters': HalfExtent * 2, 'SamplingMeters': Spacing, 'AgentRadiusMeters': 0.55, 'MaximumSlopeDegrees': 28, 'WadingDepthMeters': 0.28, 'FoundationRadiusMeters': 1.4, 'FoundationMaximumSlopeDegrees': 10, 'FoundationMaximumReliefMeters': 0.45, 'MinimumBuildableAreaSquareMeters': 140, 'WaterLevelsMeters': [0, 0.6], 'Rows': Rows, 'ElapsedSeconds': round(time.perf_counter() - Started, 2)}
    (OutputDirectory / 'BatchResults.json').write_text(json.dumps(Summary, ensure_ascii=False, indent=2), encoding='utf-8')
    (OutputDirectory / 'PreviewData.json').write_text(json.dumps(Previews, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    print(f"Completed {len(Rows)} terrains and {len(Rows) * 2} geometric evaluations in {Summary['ElapsedSeconds']} seconds.")


if __name__ == '__main__':
    Main()
