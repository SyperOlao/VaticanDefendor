"""Install a player view and profile without rewriting existing business charts."""
from datetime import datetime, timedelta, timezone
import fcntl
import json
import os
from pathlib import Path

from bootstrap import provision_superset
from player_catalog import PLAYER_DATASET, COLUMN_LABELS, activity_sql, board
from presentation import identity
from production_presentation import DESCRIPTION_CSS
from serving import identifier


def provision_player_dashboard(admin, target, reader, *, demo=False, reference_at=None, superset_app=None):
    admin.command(f'CREATE OR REPLACE VIEW {identifier(target)}.player_activity SQL SECURITY DEFINER AS '+activity_sql(target,demo))
    spec=board(demo,reference_at)
    reference=reference_at or datetime.now(timezone.utc)
    day=reference.replace(hour=0,minute=0,second=0,microsecond=0)
    start,end=day-timedelta(days=29),day+timedelta(days=1)
    players=admin.query(f'SELECT player_id, count() AS records FROM {identifier(target)}.player_activity '
        'WHERE occurred_at >= {start:DateTime64(3)} AND occurred_at < {end:DateTime64(3)} '
        'GROUP BY player_id ORDER BY records DESC, player_id LIMIT 1',parameters={'start':start,'end':end}).result_rows
    default=[players[0][0]] if players else []
    namespace='demo' if demo else ''
    period=f'{start:%Y-%m-%d} : {end:%Y-%m-%d}' if demo else None
    if demo:
        from demo_presentation import boards
        navigation=boards(reference_at)+[spec]
    else:
        from production_presentation import boards
        navigation=boards()+[spec]
    app=provision_superset(target,reader,boards=[spec],namespace=namespace,period=period,
        title_prefix='ДЕМО · ' if demo else '', chart_title_prefix='',plain_legends=True,
        column_labels={PLAYER_DATASET.name:COLUMN_LABELS}, dataset_specs=[PLAYER_DATASET],
        navigation_boards=navigation, filter_defaults={spec.slug:default},dashboard_css=DESCRIPTION_CSS,
        superset_app=superset_app)

    # Add discoverable navigation to managed dashboards; preserve all chart parameters.
    with app.app_context():
        from superset import db
        from superset.models.dashboard import Dashboard
        for other in navigation[:-1]:
            key=namespace+':'+other.slug if namespace else other.slug
            dashboard=db.session.query(Dashboard).filter_by(uuid=identity('dashboard',key)).one_or_none()
            if dashboard is None:
                continue
            positions=json.loads(dashboard.position_json)
            intro=positions.get('MARKDOWN-INTRO',{}).get('meta',{})
            link=f'[Игрок](/superset/dashboard/{spec.slug}/)'
            if intro.get('code'):
                text=intro['code'].replace(' · '+link,'')
                parts=text.split('\n\n')
                nav_part=1 if len(parts)>1 and '/superset/dashboard/' in parts[1] else 0
                parts[nav_part]+=' · '+link
                intro['code']='\n\n'.join(parts)
                dashboard.position_json=json.dumps(positions,ensure_ascii=False)
        db.session.commit()
    print('Player dashboard ready: /superset/dashboard/'+spec.slug+'/',flush=True)
    return app


if __name__=='__main__':
    from verify import client
    with Path('/app/superset_home/vatican-init.lock').open('w') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        admin=client()
        try:
            app=provision_player_dashboard(admin,os.getenv('VD_SUPERSET_BI_DATABASE','vatican_bi'),
                os.getenv('VD_SUPERSET_CLICKHOUSE_USER','vatican_superset'))
            exists=admin.query("SELECT count() FROM system.tables WHERE database='vatican_analytics_demo' AND name='demo_seed_manifest'").first_row[0]
            manifest=admin.query("SELECT reference_at FROM vatican_analytics_demo.demo_seed_manifest FINAL WHERE seed_key='vatican-demo' AND state='ready'").result_rows if exists else []
            if manifest:
                from demo_data import utc_reference
                provision_player_dashboard(admin,'vatican_bi_demo',os.getenv('VD_SUPERSET_CLICKHOUSE_USER','vatican_superset'),
                    demo=True,reference_at=utc_reference(manifest[0][0]),superset_app=app)
        finally:
            admin.close()
