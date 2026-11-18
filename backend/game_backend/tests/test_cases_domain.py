from copy import deepcopy
from uuid import uuid4
import pytest
from vatican_backend.domain.cases import CaseRules, seeded_randbelow


def configuration():
    bundle=lambda: {'schemaVersion':1,'bundleId':str(uuid4()),'items':[
        {'itemIndex':0,'rewardType':'CURRENCY','rewardRef':'GEMS','quantity':1}]}
    return {'schemaVersion':1,'caseId':str(uuid4()),'code':'test.case','rulesetId':'rules-v1','priceCurrency':'GEMS',
        'price':10,'pityThreshold':3,'guaranteedRarity':'RARE','drops':[
            {'entryIndex':0,'rarity':'COMMON','weight':9,'reward':bundle()},
            {'entryIndex':1,'rarity':'RARE','weight':1,'reward':bundle()}]}


def test_weight_boundaries_natural_guarantee_and_forced_threshold():
    rules=CaseRules.parse(configuration())
    assert rules.draw(0,lambda upper:8)[0].rarity=='COMMON'
    rare,after,guaranteed,_,_=rules.draw(1,lambda upper:9)
    assert rare.rarity=='RARE' and after==0 and not guaranteed
    forced,after,guaranteed,_,total=rules.draw(2,lambda upper:0)
    assert forced.rarity=='RARE' and after==0 and guaranteed and total==1
    with pytest.raises(ValueError):rules.draw(0,lambda upper:upper)
    with pytest.raises(ValueError):rules.draw(0,lambda upper:True)


@pytest.mark.parametrize('patch',[{'price':-1},{'price':True},{'priceCurrency':'UNKNOWN'},
    {'pityThreshold':0},{'pityThreshold':True},{'guaranteedRarity':'ABSENT'},{'drops':[]}])
def test_invalid_case_configuration_rejected(patch):
    value=configuration();value.update(patch)
    with pytest.raises(ValueError):CaseRules.parse(value)


def test_invalid_entries_and_unsupported_rewards_are_rejected_as_whole_configuration():
    value=configuration()
    for patch in ({'weight':0},{'weight':2**63},{'entryIndex':-1},{'rarity':None}):
        invalid=deepcopy(value);invalid['drops'][0].update(patch)
        with pytest.raises(ValueError):CaseRules.parse(invalid)
    invalid=deepcopy(value);invalid['drops'][1]['entryIndex']=0
    with pytest.raises(ValueError):CaseRules.parse(invalid)
    invalid=deepcopy(value);invalid['drops'][0]['reward']['items'][0]['rewardType']='UNKNOWN'
    with pytest.raises(ValueError):CaseRules.parse(invalid)


def test_seed_is_deterministic_and_supports_int64_weights():
    seed=bytes(range(32))
    for upper in (1,10,2**63-1):
        first=seeded_randbelow(seed,upper)
        assert 0<=first<upper and seeded_randbelow(seed,upper)==first
    with pytest.raises(ValueError):seeded_randbelow(b'bad',10)


def test_funding_acknowledgement_must_prove_matching_source_and_consistent_outcome():
    from vatican_backend.domain.cases import CASE_CONSUME, CASE_PAYMENT, validate_case_receipt
    base={'accountId':str(uuid4()),'openingId':str(uuid4()),'caseCode':'test.case'}
    inventory={**base,'quantity':1}
    receipt={**inventory,'itemId':'case:test.case','outcome':'REJECTED','code':'InsufficientCases','quantityAfter':0}
    validate_case_receipt(CASE_CONSUME,inventory,receipt)
    for patch in ({'quantityAfter':1},{'quantity':True},{'openingId':str(uuid4())}):
        with pytest.raises(ValueError):validate_case_receipt(CASE_CONSUME,inventory,{**receipt,**patch})
    wallet={**base,'currencyId':'GEMS','amount':10}
    receipt={**wallet,'outcome':'REJECTED','code':'InsufficientFunds','balanceAfter':5}
    validate_case_receipt(CASE_PAYMENT,wallet,receipt)
    for patch in ({'balanceAfter':10},{'amount':True},{'outcome':'APPLIED'}):
        with pytest.raises((ValueError,KeyError)):validate_case_receipt(CASE_PAYMENT,wallet,{**receipt,**patch})
