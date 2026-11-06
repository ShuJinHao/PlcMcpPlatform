"""Install original JSON test data for the separate fixture host. No vendor files are generated."""
from pathlib import Path
import argparse
import json

parser = argparse.ArgumentParser()
parser.add_argument('--workspace-root', required=True)
args = parser.parse_args()
root = Path(args.workspace_root).expanduser().resolve()
template = root / 'templates' / 'sample'
if template.exists():
    raise SystemExit('The sample template already exists; this command never overwrites it.')
template.mkdir(parents=True)
fixture = json.loads((Path(__file__).resolve().parents[1] / 'tests/fixtures/sample.fixture.json').read_text(encoding='utf-8'))
fixture['compileError'] = False
(template / 'project.fixture.json').write_text(json.dumps(fixture, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
metadata = dict(id='sample', projectFile='project.fixture.json', files=['project.fixture.json'],
                ideProfile='fixture', targetController='test-only', requiredLibraries=[], executionMode='fixture')
(template / 'template.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
print('Created simulated template: ' + str(template))
print('This JSON is test data, not an InoProShop project. Use the separate TestHost.')
