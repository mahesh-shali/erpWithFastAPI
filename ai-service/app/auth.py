"""Validates the JWT issued by the ASP.NET Core API (shared HS256 secret)."""
from dataclasses import dataclass
from uuid import UUID

import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from .config import Settings, get_settings

_bearer = HTTPBearer(auto_error=False)


@dataclass(frozen=True)
class Principal:
    user_id: UUID
    org_id: UUID
    role: str
    session_id: UUID  # "sid": the login session; checked against the DB per request (see SessionGuard)


def decode_token(token: str, s: Settings) -> Principal:
    try:
        claims = jwt.decode(
            token,
            s.jwt_secret,
            algorithms=["HS256"],
            audience=s.jwt_audience,
            issuer=s.jwt_issuer,
            options={"require": ["exp", "sub", "org_id", "sid"]},
        )
        return Principal(UUID(claims["sub"]), UUID(claims["org_id"]), claims.get("role", "User"),
                         UUID(claims["sid"]))
    except (jwt.PyJWTError, ValueError, KeyError) as e:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid or expired token") from e


def current_principal(
    creds: HTTPAuthorizationCredentials | None = Depends(_bearer),
    s: Settings = Depends(get_settings),
) -> Principal:
    if creds is None or creds.scheme.lower() != "bearer":
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Missing bearer token")
    return decode_token(creds.credentials, s)
