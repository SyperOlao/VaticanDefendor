from uuid import uuid5

from .catalog import MessageStatus
from .errors import ApplicationError
from .ports import DefinitionsGateway, UnitOfWorkFactory
from ..domain.run_rewards import RunReward
from ..domain.weapons import definition_map


class RunRewardConsumer:
    def __init__(self, uow: UnitOfWorkFactory, definitions: DefinitionsGateway):
        self.uow, self.definitions = uow, definitions

    @staticmethod
    def _prior(repo, message_id, reward):
        repo.lock_message(message_id)
        repo.lock_player(uuid5(reward.run_id, "inventory.run.reward"))
        repo.lock_player(reward.account_id)
        consumed = repo.received(message_id, reward.digest)
        receipt = repo.run_receipt(reward.run_id, reward.account_id, reward.configuration_id, reward.digest)
        if consumed and receipt is None:
            raise ApplicationError(409, "Run reward message has no source receipt")
        if receipt is not None and not consumed:
            repo.record_message(message_id, reward.digest)
        return receipt

    def receive(self, message_id, event_name, schema_version, payload):
        try:
            reward = RunReward.parse(event_name, schema_version, payload)
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(422, "Invalid run weapon reward") from None
        with self.uow() as work:
            receipt = self._prior(work.repository, message_id, reward)
            if receipt is not None:
                return {"messageId": str(message_id), "status": MessageStatus.APPLIED, "receipt": receipt}
        # Fetch content after releasing the replay transaction. Pinned progression never uses this catalog.
        try:
            definitions = definition_map(self.definitions.get())
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Game content is invalid") from None
        if reward.configuration_id not in definitions:
            raise ApplicationError(422, "Unknown reward configuration")
        with self.uow() as work:
            repo = work.repository
            receipt = self._prior(repo, message_id, reward)
            if receipt is None:
                repo.ensure(reward.account_id)
                for pinned in reward.weapons:
                    weapon = repo.weapon(reward.account_id, pinned.instance_id)
                    if weapon is None or weapon.configuration_id != pinned.configuration_id:
                        raise ApplicationError(409, "Pinned weapon is not owned by the player")
                    try:
                        level, experience = pinned.progression.advance(weapon.level, weapon.experience)
                    except ValueError:
                        raise ApplicationError(409, "Weapon experience capacity exceeded") from None
                    repo.set_progress(weapon.instance_id, level, experience)
                instance_id, created = repo.grant(reward.account_id, "run:" + str(reward.run_id), reward.configuration_id)
                if not created:
                    # All historical grants must be covered by the migration's source receipts.
                    raise ApplicationError(409, "Run reward is missing its source receipt")
                receipt = {"accountId": str(reward.account_id), "runId": str(reward.run_id),
                           "configurationId": reward.configuration_id, "instanceId": str(instance_id),
                           "inventoryRevision": repo.state(reward.account_id).revision}
                repo.record_run_receipt(reward.run_id, reward.account_id, reward.configuration_id, reward.digest, receipt)
                repo.record_message(message_id, reward.digest)
            return {"messageId": str(message_id), "status": MessageStatus.APPLIED, "receipt": receipt}
