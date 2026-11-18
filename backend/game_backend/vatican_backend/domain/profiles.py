"""Player-owned presentation and preferences; no credentials or economy state."""
from dataclasses import dataclass
import re
import unicodedata
from uuid import UUID
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


@dataclass(frozen=True)
class ProfileDetails:
    account_id: UUID
    version: int
    display_name: str
    avatar_asset_key: str | None
    display_timezone_iana: str
    locale: str


@dataclass(frozen=True)
class AvatarOption:
    asset_key: str
    title: str
    symbol: str
    background_color: str


def avatar_catalog(values):
    if not isinstance(values,list) or not 1<=len(values)<=100:raise ValueError('Invalid avatar catalog')
    result=[]
    for value in values:
        if not isinstance(value,dict) or set(value)!={'assetKey','title','symbol','backgroundColor'}:
            raise ValueError('Invalid avatar catalog fields')
        validate_changes(1,{'avatarAssetKey':value['assetKey']})
        if (value['assetKey'] is None or not isinstance(value['title'],str) or not 1<=len(value['title'])<=40
                or not isinstance(value['symbol'],str) or not 1<=len(value['symbol'])<=2
                or any(unicodedata.category(c).startswith('C') for c in value['symbol']+value['title'])
                or not isinstance(value['backgroundColor'],str) or re.fullmatch(r'#[0-9a-fA-F]{6}',value['backgroundColor']) is None):
            raise ValueError('Invalid avatar presentation')
        result.append(AvatarOption(value['assetKey'],value['title'],value['symbol'],value['backgroundColor']))
    if len({v.asset_key for v in result})!=len(result):raise ValueError('Duplicate avatar asset key')
    return tuple(result)


def validate_changes(expected_version, changes):
    if type(expected_version) is not int or not 1 <= expected_version < 2_147_483_647:
        raise ValueError('Invalid expectedVersion')
    allowed={'displayName','avatarAssetKey','displayTimezoneIana','locale'}
    if not isinstance(changes,dict) or not changes or not set(changes)<=allowed:
        raise ValueError('Provide at least one editable profile field')
    result=dict(changes)
    if 'displayName' in result:
        value=result['displayName']
        if not isinstance(value,str):raise ValueError('Invalid displayName')
        value=unicodedata.normalize('NFC',value).strip()
        if (not value or len(value)>32 or any(unicodedata.category(c).startswith('C') for c in value)
                or len(value.encode('utf-8'))>120):raise ValueError('Invalid displayName')
        result['displayName']=value
    if 'avatarAssetKey' in result:
        value=result['avatarAssetKey']
        if value is not None and (not isinstance(value,str)
                or re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_./-]{0,127}',value) is None
                or any(part in ('','.','..') for part in value.split('/'))):
            raise ValueError('Invalid avatarAssetKey')
    if 'locale' in result:
        value=result['locale']
        if not isinstance(value,str) or len(value)>16 or re.fullmatch(r'[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*',value) is None:
            raise ValueError('Invalid locale')
    if 'displayTimezoneIana' in result:
        value=result['displayTimezoneIana']
        if not isinstance(value,str) or not 1<=len(value)<=64:raise ValueError('Invalid displayTimezoneIana')
        try:ZoneInfo(value)
        except (ValueError,ZoneInfoNotFoundError):raise ValueError('Invalid displayTimezoneIana') from None
    return result
