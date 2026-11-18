import json
from pathlib import Path
from ..domain.profiles import avatar_catalog


def load_avatars(filename):
    path=Path(filename)
    if not path.is_absolute():path=Path(__file__).resolve().parents[2]/path
    return avatar_catalog(json.loads(path.read_text(encoding='utf-8')))
