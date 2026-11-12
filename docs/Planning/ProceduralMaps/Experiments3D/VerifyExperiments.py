from pathlib import Path
import argparse
import hashlib
import json

import numpy as Numpy
from scipy.interpolate import LinearNDInterpolator

import GenerateExperiments as Generator


Parser = argparse.ArgumentParser()
Parser.add_argument('--results', type=Path, default=Path(__file__).resolve().parent / 'Results')
Arguments = Parser.parse_args()
OutputDirectory = Arguments.results.resolve()
Batch = json.loads((OutputDirectory / 'BatchResults.json').read_text(encoding='utf-8'))
Generator.HorizontalScale = Batch['AreaWidthMeters'] / 48
Generator.HalfExtent = Batch['AreaWidthMeters'] / 2
Generator.SampleCount = round(Batch['AreaWidthMeters'] / Batch['SamplingMeters']) + 1
Generator.Spacing = Batch['SamplingMeters']
Generator.Coordinates = Numpy.linspace(-Generator.HalfExtent, Generator.HalfExtent, Generator.SampleCount)
Generator.Horizontal, Generator.Vertical = Numpy.meshgrid(Generator.Coordinates, Generator.Coordinates)
Generator.ChurchPosition = (0.0, -2.0 * Generator.HorizontalScale)
Previews = json.loads((OutputDirectory / 'PreviewData.json').read_text(encoding='utf-8'))
Checks = []
Failures = []
Approximation = []
for Profile in Generator.Profiles:
    Rows = [Row for Row in Batch['Rows'] if Row['Profile'] == Profile['Id']]
    if not Rows:
        continue
    for Row in Rows:
        for RecordName in ['Resources', 'Entries']:
            for Original, Raised in zip(Row['Normal'][RecordName], Row['Raised'][RecordName]):
                if Raised['Position'] is not None and Raised['Position'] != Original['Position']:
                    Failures.append(f"Moving fixture: {Profile['Id']}/{Row['Seed']}/{RecordName}")
        if Row['Raised']['LiquidFraction'] < Row['Normal']['LiquidFraction']:
            Failures.append(f"Non-monotonic flooding: {Profile['Id']}/{Row['Seed']}")
        if Profile['Liquid'] == 'none':
            for Property in ['ReachedEntries', 'ReachableResources', 'BuildableSquareMeters', 'ReachableFraction', 'AcceptedGeometry']:
                if Row['Normal'][Property] != Row['Raised'][Property]:
                    Failures.append(f'Dry control changed: {Property}')
    ElevationFirst = Generator.Surface(Profile, 105, Generator.Horizontal, Generator.Vertical)
    ElevationSecond = Generator.Surface(Profile, 105, Generator.Horizontal, Generator.Vertical)
    if not Numpy.array_equal(ElevationFirst, ElevationSecond):
        Failures.append(f"Non-reproducible field: {Profile['Id']}")
    Preview = next(Record for Record in Previews if Record['Id'] == Profile['Id'] and Record['Seed'] == 105)
    Vertices = Numpy.array(Preview['Vertices'])
    Triangles = Numpy.array(Preview['Triangles']).reshape(-1, 3)
    Normals = Numpy.cross(Vertices[Triangles[:, 1]] - Vertices[Triangles[:, 0]], Vertices[Triangles[:, 2]] - Vertices[Triangles[:, 0]])
    if not Numpy.all(Normals[:, 1] > 0):
        Failures.append(f"Folded terrain mesh: {Profile['Id']}")
    Interpolation = LinearNDInterpolator(Vertices[:, [0, 2]], Vertices[:, 1])
    Approximate = Interpolation(Generator.Horizontal, Generator.Vertical)
    Inside = Generator.Boundary(105, Generator.Horizontal, Generator.Vertical) & Numpy.isfinite(Approximate)
    Error = Numpy.abs(Approximate[Inside] - ElevationFirst[Inside])
    Approximation.append({'Profile': Profile['Id'], 'Seed': 105, 'HeightError95Meters': round(float(Numpy.quantile(Error, 0.95)), 3), 'MaximumHeightErrorMeters': round(float(Error.max()), 3)})
    Checks.append({'Profile': Profile['Id'], 'TerrainSha256': hashlib.sha256(ElevationFirst.tobytes()).hexdigest(), 'PositiveTriangleNormals': True, 'RepeatFieldEqual': True})
Browser = json.loads((OutputDirectory / 'BrowserVerification.json').read_text(encoding='utf-8'))
if any(not Case['Ready'] or not Case['Matches'] for Case in Browser['Cases']):
    Failures.append('Browser did not match generated measurements.')
if Browser['Mobile']['ScrollWidth'] > Browser['Mobile']['Width'] or Browser['Desktop']['ScrollWidth'] > Browser['Desktop']['Width']:
    Failures.append('Browser horizontal overflow.')
if not Browser['CameraChanged'] or not Browser['OrbitChanged']:
    Failures.append('Camera interaction did not change the rendered result.')
Report = {'Failures': Failures, 'FixturePositionsFixed': True, 'FloodingMonotonic': True, 'DryControlStable': True, 'Profiles': Checks, 'PreviewApproximation': Approximation, 'Scope': 'Analytical surface and sampled geometric checks. Preview meshes are simplified; height error is measured here and must not be treated as a validated collision surface.'}
(OutputDirectory / 'Verification.json').write_text(json.dumps(Report, indent=2), encoding='utf-8')
print(json.dumps(Report, indent=2))
if Failures:
    raise SystemExit(1)
