from copy import deepcopy
from uuid import uuid4
import pytest
from vatican_backend.domain.level_rewards import RewardPlan,message_id,validate_receipt


def plan():
    return {'schemaVersion':1,'bundleId':str(uuid4()),'items':[
        {'itemIndex':0,'rewardType':'CURRENCY','rewardRef':'PRESTIGE','quantity':10},
        {'itemIndex':3,'rewardType':'WEAPON','rewardRef':'sword.common','quantity':2}]}


def test_plan_roundtrip_and_stable_per_line_identity():
    value=plan()
    assert RewardPlan.parse(value).snapshot()==value
    identity=uuid4()
    assert message_id(identity,0)==message_id(identity,0)
    assert message_id(identity,0)!=message_id(identity,3)


@pytest.mark.parametrize('patch',[{'quantity':0},{'quantity':True},{'quantity':-1},{'quantity':2**63},
    {'rewardType':'EFFECT'},{'rewardRef':'UNSUPPORTED'},{'itemIndex':-1}])
def test_invalid_lines_are_rejected_before_dispatch(patch):
    value=plan()
    value['items'][0].update(patch)
    with pytest.raises(ValueError):
        RewardPlan.parse(value)


def test_limits_and_duplicate_lines_are_rejected():
    for lines in ([],[plan()['items'][0]]*2,[{**plan()['items'][1],'quantity':101}]):
        value=plan();value['items']=lines
        with pytest.raises(ValueError):
            RewardPlan.parse(value)


def test_acknowledgement_must_match_the_reward_source_and_actual_instance_count():
    payload={'accountId':str(uuid4()),'claimId':str(uuid4()),'itemIndex':3,
             'rewardType':'WEAPON','rewardRef':'sword.common','quantity':2}
    correct={**payload,'weaponInstanceIds':[str(uuid4()),str(uuid4())]}
    validate_receipt(payload,correct)
    for patch in ({'quantity':True},{'claimId':str(uuid4())},{'weaponInstanceIds':correct['weaponInstanceIds'][:1]},
                  {'weaponInstanceIds':[correct['weaponInstanceIds'][0]]*2}):
        with pytest.raises((ValueError,TypeError)):
            validate_receipt(payload,{**correct,**patch})
