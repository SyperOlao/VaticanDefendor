"""Write ignored, local cross-language grant fixtures for the Unity Editor check."""

import json
import sys
from pathlib import Path
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vatican_backend.grants import issue_grant
from vatican_backend.settings import ServerSlot


def main(config_path: Path, output_path: Path) -> None:
    config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    account_id, run_id = uuid4(), uuid4()
    server_id = uuid4()
    slot = ServerSlot(server_id, "127.0.0.1", 7979, config["serverSecret"])
    other_slot = ServerSlot(uuid4(), "127.0.0.1", 7980, config["serverSecret"])
    def token(account=account_id, server=slot, generation=1, ttl=120):
        return issue_grant(account, run_id, server, generation, ttl)[0]

    cases = {
        "accountId": str(account_id),
        "runId": str(run_id),
        "serverId": str(server_id),
        "secret": config["serverSecret"],
        "valid": token(),
        "expired": token(ttl=-1),
        "foreignAccount": token(account=uuid4()),
        "foreignServer": token(server=other_slot),
        "staleGeneration": token(generation=2),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(cases), encoding="utf-8")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
