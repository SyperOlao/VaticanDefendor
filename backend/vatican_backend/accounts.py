import hashlib
import hmac
import secrets
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from argon2 import PasswordHasher
from argon2.exceptions import VerificationError, InvalidHashError
from fastapi import Depends, Header, HTTPException, Request
from pydantic import BaseModel, ConfigDict, Field


class Credentials(BaseModel):
    model_config = ConfigDict(extra="forbid")
    login: str = Field(pattern=r"^[A-Za-z0-9_.-]{3,64}$")
    password: str = Field(min_length=6, max_length=128)


class Recovery(Credentials):
    recoveryCode: str = Field(min_length=32, max_length=128)


class Refresh(BaseModel):
    model_config = ConfigDict(extra="forbid")
    refreshToken: str = Field(min_length=43, max_length=43)


def digest(value):
    return hashlib.sha256(value.encode("utf-8")).digest()


def install_accounts(app, connect, account_from_bearer):
    hasher = PasswordHasher(time_cost=3, memory_cost=65536, parallelism=1)
    dummy_hash = hasher.hash(secrets.token_urlsafe(32))

    def limit(request, login):
        # Shared across API workers. Do not trust client-controlled forwarded headers.
        keys = [("ip:" + (request.client.host if request.client else "unknown"), 60),
                ("login:" + login, 10)]
        denied = False
        with connect() as db:
            for key, maximum in sorted(keys):
                row = db.execute(
                    """INSERT INTO auth_attempts(key_hash,attempts) VALUES (%s,1)
                       ON CONFLICT(key_hash) DO UPDATE SET
                       attempts=CASE WHEN auth_attempts.window_start<now()-interval '5 minutes'
                         THEN 1 ELSE auth_attempts.attempts+1 END,
                       window_start=CASE WHEN auth_attempts.window_start<now()-interval '5 minutes'
                         THEN now() ELSE auth_attempts.window_start END RETURNING attempts""",
                    (digest(key),)).fetchone()
                denied |= row["attempts"] > maximum
            db.execute("DELETE FROM auth_attempts WHERE window_start<now()-interval '1 day'")
        if denied:
            raise HTTPException(429, "Too many authentication attempts; retry later",
                                headers={"Retry-After": "300"})

    def session(db, account_id):
        access, refresh = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
        expires = datetime.now(timezone.utc) + timedelta(minutes=15)
        db.execute("""INSERT INTO auth_sessions(id,account_id,access_hash,access_expires_at,
                      refresh_hash,refresh_expires_at) VALUES (%s,%s,%s,%s,%s,now()+interval '30 days')""",
                   (uuid4(), account_id, digest(access), expires, digest(refresh)))
        return {"accountId": str(account_id), "accessToken": access,
                "refreshToken": refresh, "expiresAt": expires.isoformat(), "expiresIn": 900}

    @app.post("/v1/auth/register", status_code=201)
    def register(body: Credentials, request: Request, authorization: str | None = Header(default=None)):
        login = body.login.lower()
        limit(request, login)
        account_id = account_from_bearer(authorization) if authorization else uuid4()
        password_hash = hasher.hash(body.password)
        recovery = secrets.token_urlsafe(32)
        with connect() as db:
            with db.transaction():
                # Serialize registration for the same login without holding a password-hashing lock.
                db.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s,0))", (login,))
                if db.execute("SELECT 1 FROM account_logins WHERE login=%s", (login,)).fetchone():
                    raise HTTPException(409, "Login is unavailable")
                if authorization:
                    db.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (account_id,))
                    if db.execute("SELECT 1 FROM account_logins WHERE account_id=%s", (account_id,)).fetchone():
                        raise HTTPException(409, "Account already has a login")
                else:
                    db.execute("INSERT INTO accounts(id) VALUES (%s)", (account_id,))
                    db.execute("INSERT INTO profiles(account_id,version,display_name) VALUES (%s,1,%s)",
                               (account_id, body.login[:32]))
                db.execute("INSERT INTO account_logins(login,account_id,password_hash,recovery_hash) VALUES (%s,%s,%s,%s)",
                           (login, account_id, password_hash, digest(recovery)))
                db.execute("DELETE FROM guest_credentials WHERE account_id=%s", (account_id,))
                result = session(db, account_id)
        return {**result, "recoveryCode": recovery}

    @app.post("/v1/auth/login")
    def login(body: Credentials, request: Request):
        name = body.login.lower()
        limit(request, name)
        with connect() as db:
            row = db.execute("SELECT * FROM account_logins WHERE login=%s", (name,)).fetchone()
        try:
            hasher.verify(row["password_hash"] if row else dummy_hash, body.password)
        except (VerificationError, InvalidHashError):
            raise HTTPException(401, "Invalid login or password") from None
        if row is None:
            raise HTTPException(401, "Invalid login or password")
        with connect() as db:
            with db.transaction():
                current = db.execute("SELECT password_hash FROM account_logins WHERE login=%s FOR UPDATE",
                                     (name,)).fetchone()
                if current["password_hash"] != row["password_hash"]:
                    raise HTTPException(401, "Credentials changed; retry login")
                return session(db, row["account_id"])

    @app.post("/v1/auth/refresh")
    def refresh(body: Refresh):
        with connect() as db:
            with db.transaction():
                owner = db.execute("SELECT account_id FROM auth_sessions WHERE refresh_hash=%s",
                                   (digest(body.refreshToken),)).fetchone()
                if owner is None:
                    raise HTTPException(401, "Invalid or expired refresh token")
                # Recovery and refresh use the same lock order.
                db.execute("SELECT account_id FROM account_logins WHERE account_id=%s FOR UPDATE",
                           (owner["account_id"],))
                row = db.execute("SELECT * FROM auth_sessions WHERE refresh_hash=%s FOR UPDATE",
                                 (digest(body.refreshToken),)).fetchone()
                if row is None or row["revoked_at"] is not None or row["refresh_expires_at"] <= datetime.now(timezone.utc):
                    raise HTTPException(401, "Invalid or expired refresh token")
                db.execute("UPDATE auth_sessions SET revoked_at=now() WHERE id=%s", (row["id"],))
                return session(db, row["account_id"])

    @app.post("/v1/auth/logout", status_code=204)
    def logout(account_id=Depends(account_from_bearer), authorization: str = Header()):
        with connect() as db:
            db.execute("UPDATE auth_sessions SET revoked_at=now() WHERE access_hash=%s AND account_id=%s",
                       (digest(authorization[7:]), account_id))
            db.execute("DELETE FROM guest_credentials WHERE token_hash=%s AND account_id=%s",
                       (digest(authorization[7:]), account_id))

    @app.post("/v1/auth/recover")
    def recover(body: Recovery, request: Request):
        name = body.login.lower()
        limit(request, name)
        password_hash = hasher.hash(body.password)
        next_code = secrets.token_urlsafe(32)
        with connect() as db:
            with db.transaction():
                row = db.execute("SELECT * FROM account_logins WHERE login=%s FOR UPDATE", (name,)).fetchone()
                if row is None or not hmac.compare_digest(row["recovery_hash"], digest(body.recoveryCode)):
                    raise HTTPException(401, "Invalid login or recovery code")
                db.execute("UPDATE account_logins SET password_hash=%s,recovery_hash=%s,updated_at=now() WHERE login=%s",
                           (password_hash, digest(next_code), name))
                db.execute("UPDATE auth_sessions SET revoked_at=now() WHERE account_id=%s", (row["account_id"],))
                db.execute("DELETE FROM guest_credentials WHERE account_id=%s", (row["account_id"],))
                result = session(db, row["account_id"])
        return {**result, "recoveryCode": next_code}
