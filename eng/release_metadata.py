import json
from pathlib import Path
import re


def metadata():
    value = json.loads((Path(__file__).resolve().parents[1]/'release/version.json').read_text(encoding='utf-8'))
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-rc\.[1-9][0-9]*', value.get('version', '')) or value.get('prerelease') is not True:
        raise ValueError('An explicit release-candidate version is required.')
    if value.get('realIdeValidated') is not False or value.get('plcRuntimeValidated') is not False:
        raise ValueError('Release tooling cannot promote IDE/PLC validation.')
    return value
