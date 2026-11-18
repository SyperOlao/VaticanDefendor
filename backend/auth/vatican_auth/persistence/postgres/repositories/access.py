from datetime import datetime
from uuid import UUID

from sqlalchemy import delete, func, select, update
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import PermissionRecord, RoleRecord
from ..tables import Account, AccountRole, Permission, Role, RolePermission


class PostgresAccessRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def lock(self) -> None:
        await self.session.execute(select(func.pg_advisory_xact_lock(691750363)))

    async def roles_for(self, account_id: UUID) -> tuple[str, ...]:
        return tuple((await self.session.scalars(select(Role.code).join(AccountRole).where(
            AccountRole.account_id == account_id, AccountRole.revoked_at.is_(None),
        ).order_by(Role.code))).all())

    async def permissions_for(self, account_id: UUID) -> tuple[str, ...]:
        return tuple((await self.session.scalars(select(Permission.code).join(RolePermission).join(
            AccountRole, AccountRole.role_id == RolePermission.role_id,
        ).where(AccountRole.account_id == account_id, AccountRole.revoked_at.is_(None))
            .distinct().order_by(Permission.code))).all())

    async def _records(self, statement) -> list[RoleRecord]:
        rows = (await self.session.execute(statement)).all()
        if not rows:
            return []
        permission_rows = (await self.session.execute(select(RolePermission.role_id, Permission.code).join(
            Permission, Permission.id == RolePermission.permission_id,
        ).where(RolePermission.role_id.in_([row.id for row in rows])).order_by(Permission.code))).all()
        codes = {}
        for role_id, code in permission_rows:
            codes.setdefault(role_id, []).append(code)
        return [RoleRecord(row.id, row.code, row.description, row.is_assignable,
                           tuple(codes.get(row.id, []))) for row in rows]

    async def roles(self) -> list[RoleRecord]:
        return await self._records(select(Role.id, Role.code, Role.description, Role.is_assignable).order_by(Role.code))

    async def role(self, role_id: int) -> RoleRecord | None:
        rows = await self._records(select(Role.id, Role.code, Role.description, Role.is_assignable).where(Role.id == role_id))
        return rows[0] if rows else None

    async def role_by_code(self, code: str) -> RoleRecord | None:
        role_id = await self.session.scalar(select(Role.id).where(Role.code == code))
        return await self.role(role_id) if role_id is not None else None

    async def permissions(self) -> list[PermissionRecord]:
        rows = (await self.session.execute(select(Permission.id, Permission.code, Permission.description).order_by(Permission.code))).all()
        return [PermissionRecord(*row) for row in rows]

    async def set_role_permissions(self, role_id: int, codes: tuple[str, ...]) -> None:
        await self.session.execute(delete(RolePermission).where(RolePermission.role_id == role_id))
        ids = (await self.session.scalars(select(Permission.id).where(Permission.code.in_(codes)))).all()
        self.session.add_all([RolePermission(role_id=role_id, permission_id=permission_id) for permission_id in ids])
        await self.session.flush()

    async def create_role(self, code: str, description: str, permission_codes: tuple[str, ...], assignable: bool) -> RoleRecord:
        role = Role(code=code, description=description, is_assignable=assignable)
        self.session.add(role)
        await self.session.flush()
        await self.set_role_permissions(role.id, permission_codes)
        return await self.role(role.id)

    async def update_role(self, role_id: int, description: str, permission_codes: tuple[str, ...], assignable: bool) -> RoleRecord:
        await self.session.execute(update(Role).where(Role.id == role_id).values(description=description, is_assignable=assignable))
        await self.set_role_permissions(role_id, permission_codes)
        return await self.role(role_id)

    async def create_permission(self, code: str, description: str) -> PermissionRecord:
        permission = Permission(code=code, description=description)
        self.session.add(permission)
        await self.session.flush()
        return PermissionRecord(permission.id, permission.code, permission.description)

    async def has_role(self, account_id: UUID, role_id: int) -> bool:
        return (await self.session.scalar(select(AccountRole.id).where(
            AccountRole.account_id == account_id, AccountRole.role_id == role_id, AccountRole.revoked_at.is_(None),
        ))) is not None

    async def grant(self, account_id: UUID, role_id: int, actor_id: UUID | None) -> bool:
        if await self.has_role(account_id, role_id):
            return False
        self.session.add(AccountRole(account_id=account_id, role_id=role_id, granted_by=actor_id))
        await self.session.flush()
        return True

    async def revoke(self, account_id: UUID, role_id: int, now: datetime) -> bool:
        result = await self.session.execute(update(AccountRole).where(
            AccountRole.account_id == account_id, AccountRole.role_id == role_id, AccountRole.revoked_at.is_(None),
        ).values(revoked_at=now))
        return result.rowcount > 0

    async def active_role_account_count(self, role_id: int, account_status: str) -> int:
        return await self.session.scalar(select(func.count()).select_from(AccountRole).join(
            Account, Account.id == AccountRole.account_id,
        ).where(AccountRole.role_id == role_id, AccountRole.revoked_at.is_(None), Account.account_status == account_status))

    async def ensure_permission(self, code: str, description: str) -> PermissionRecord:
        row = (await self.session.execute(insert(Permission).values(code=code, description=description)
            .on_conflict_do_update(index_elements=[Permission.code], set_={"description": description})
            .returning(Permission.id, Permission.code, Permission.description))).one()
        return PermissionRecord(*row)

    async def ensure_role(self, code: str, description: str, is_assignable: bool) -> RoleRecord:
        role_id = await self.session.scalar(insert(Role).values(code=code, description=description, is_assignable=is_assignable)
            .on_conflict_do_update(index_elements=[Role.code], set_={"description": description, "is_assignable": is_assignable})
            .returning(Role.id))
        return await self.role(role_id)

    async def include_permission_in_roles(self, permission_id: int, role_ids: tuple[int, ...]) -> None:
        if role_ids:
            await self.session.execute(insert(RolePermission).values([
                {"role_id": role_id, "permission_id": permission_id} for role_id in role_ids
            ]).on_conflict_do_nothing(index_elements=[RolePermission.role_id, RolePermission.permission_id]))
