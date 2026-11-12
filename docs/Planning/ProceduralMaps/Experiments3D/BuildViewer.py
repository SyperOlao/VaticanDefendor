from pathlib import Path
import argparse
import json
import re


ExperimentDirectory = Path(__file__).resolve().parent
Parser = argparse.ArgumentParser()
Parser.add_argument('--results', type=Path, default=ExperimentDirectory / 'Results')
Parser.add_argument('--name', default='terrain-experiments')
Arguments = Parser.parse_args()
ResultDirectory = Arguments.results.resolve()
Template = (ExperimentDirectory / 'Viewer.fragment.html').read_text(encoding='utf-8')
Data = (ResultDirectory / 'PreviewData.json').read_text(encoding='utf-8')
Records = json.loads(Data)
AvailableProfiles = {Record['Id'] for Record in Records}
for ProfileId in ['meadow', 'river', 'lake', 'wetland', 'caldera']:
    if ProfileId not in AvailableProfiles:
        Template = re.sub(r'<option value="' + ProfileId + r'"[^>]*>[^<]*</option>', '', Template)
Fragment = Template.replace('__PREVIEW_DATA__', Data)
Destination = ResultDirectory / (Arguments.name + '.html')
Destination.write_text(Fragment, encoding='utf-8')
if Destination.stat().st_size >= 1000000:
    raise RuntimeError('Visualization exceeds the size budget.')
print(f'Viewer saved: {Destination.stat().st_size} bytes')
