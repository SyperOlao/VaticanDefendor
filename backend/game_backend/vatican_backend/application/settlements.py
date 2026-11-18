"""A single local transaction owns XP, the business receipt and reward outboxes."""
from .catalog import Outcome,RuntimeStatus
from .errors import ApplicationError
from .progression import ProgressionService
from ..domain.analytics import payload_for


def delivery_response(work,run_id):
    rating=work.messages.rating_delivery(run_id);analytics=work.messages.analytics_delivery(run_id)
    return {'ratingStatus':rating['status'],'rating':rating,'analyticsStatus':analytics['status'],'analytics':analytics}


def settle(work,run,result,payload,payload_hash,at,*,rating_board=None,analytics=False,weapon_configuration_id=None):
    if run.reward_per_church_level is None:raise ApplicationError(422,'No pinned reward policy')
    reward=run.reward_per_church_level*result.church_level if result.outcome==Outcome.VICTORY else 0
    progression=ProgressionService().settle(work,run,result.stats,result.outcome)
    profile=work.profiles.bump(run.account_id)
    if weapon_configuration_id is None:work.messages.enqueue_rewards(run,reward)
    else:work.messages.enqueue_rewards(run,reward,weapon_configuration_id)
    if rating_board is not None and progression is not None:
        work.messages.enqueue_rating(run,rating_board,progression['xpGranted'])
    if analytics:work.messages.enqueue_analytics(run.id,payload_for(run,result,result.stats.snapshot(),progression,at))
    work.runs.record_result(run_id=run.id,account_id=run.account_id,server_id=run.server_id,
        generation=run.generation,content_release_id=run.content_release_id,payload_hash=payload_hash,
        outcome=result.outcome,permanent_reward=reward,profile_version=profile.version,
        church_level=result.church_level,progression=progression,result_payload=payload)
    run.status=RuntimeStatus.COMPLETED;run.outcome=result.outcome;run.permanent_reward=reward;run.completed_at=at
    return {'runId':str(run.id),'outcome':result.outcome,'permanentReward':reward,
            'profileVersion':profile.version,'progression':progression}
