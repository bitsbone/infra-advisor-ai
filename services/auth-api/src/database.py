import os
import random
import uuid
from datetime import datetime, timezone

from sqlalchemy import Boolean, Column, DateTime, Text, create_engine, text
from sqlalchemy.dialects.postgresql import UUID
from sqlalchemy.orm import DeclarativeBase, Session, sessionmaker


DATABASE_URL: str = os.environ["DATABASE_URL"]

# Sample job roles for the "public infrastructure engineering firm" demo
# narrative — each maps 1:1 onto an existing specialist domain, so a
# targeting rule on one of these ("Civil Engineer" -> engineering) produces
# an intuitive, demoable result: pin a prompt version for a specific role
# and watch only that role's queries pick it up.
JOB_ROLES: list[str] = [
    "Civil Engineer",
    "Water & Energy Analyst",
    "Business Development Manager",
    "Contracts Administrator",
    "Program Director",
]

engine = create_engine(DATABASE_URL, pool_pre_ping=True)
SessionLocal = sessionmaker(bind=engine, autocommit=False, autoflush=False)


# ─── ORM Model ────────────────────────────────────────────────────────────────

class Base(DeclarativeBase):
    pass


class UserRow(Base):
    __tablename__ = "users"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    email = Column(Text, unique=True, nullable=False)
    password_hash = Column(Text, nullable=False)
    is_admin = Column(Boolean, nullable=False, default=False)
    is_service_account = Column(Boolean, nullable=False, default=False)
    created_at = Column(
        DateTime(timezone=True),
        nullable=False,
        default=lambda: datetime.now(timezone.utc),
    )
    reset_token_hash = Column(Text, nullable=True)
    reset_token_expires = Column(DateTime(timezone=True), nullable=True)
    # Demo OpenFeature targeting attribute — one of JOB_ROLES below, embedded
    # in the JWT at login so both agent-api backends can use it as a
    # targeting_key/attribute for the __llmobs__.prompt.<prompt_id> Feature
    # Flag without a network round-trip. See main.py's AdminTab endpoints.
    job_role = Column(Text, nullable=True)


# ─── Schema bootstrap ─────────────────────────────────────────────────────────

def init_db() -> None:
    """Create the users table if it does not already exist, and migrate existing tables."""
    with engine.connect() as conn:
        conn.execute(text("""
            CREATE TABLE IF NOT EXISTS users (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                email TEXT UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                is_admin BOOLEAN NOT NULL DEFAULT FALSE,
                is_service_account BOOLEAN NOT NULL DEFAULT FALSE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                reset_token_hash TEXT,
                reset_token_expires TIMESTAMPTZ
            )
        """))
        # Add reset/job_role columns to existing tables (idempotent)
        for col, typedef in [
            ("reset_token_hash", "TEXT"),
            ("reset_token_expires", "TIMESTAMPTZ"),
            ("job_role", "TEXT"),
        ]:
            conn.execute(text(
                f"ALTER TABLE users ADD COLUMN IF NOT EXISTS {col} {typedef}"
            ))
        conn.commit()

    # Backfill a random demo job_role onto any existing user who doesn't
    # have one yet (new signups get one explicitly at creation — see
    # create_user). Safe to run on every startup: only touches NULLs. Done
    # in Python (one UPDATE per row) rather than a single random-array SQL
    # expression — simpler and avoids Postgres-array-literal edge cases for
    # a one-time, small-N startup migration.
    with SessionLocal() as db:
        rows = db.query(UserRow).filter(UserRow.job_role.is_(None)).all()
        for row in rows:
            row.job_role = random.choice(JOB_ROLES)
        if rows:
            db.commit()


# ─── CRUD helpers ─────────────────────────────────────────────────────────────

def _row_to_dict(row: UserRow) -> dict:
    return {
        "id": str(row.id),
        "email": row.email,
        "password_hash": row.password_hash,
        "is_admin": row.is_admin,
        "is_service_account": row.is_service_account,
        "created_at": row.created_at.isoformat() if row.created_at else None,
        "job_role": row.job_role,
    }


def get_db() -> Session:
    return SessionLocal()


def create_user(
    email: str,
    password_hash: str,
    is_admin: bool = False,
    is_service_account: bool = False,
) -> dict:
    db: Session = get_db()
    try:
        user = UserRow(
            email=email,
            password_hash=password_hash,
            is_admin=is_admin,
            is_service_account=is_service_account,
            job_role=random.choice(JOB_ROLES),
        )
        db.add(user)
        db.commit()
        db.refresh(user)
        return _row_to_dict(user)
    finally:
        db.close()


def get_user_by_email(email: str) -> dict | None:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.email == email).first()
        return _row_to_dict(row) if row else None
    finally:
        db.close()


def get_user_by_id(user_id: str) -> dict | None:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.id == user_id).first()
        return _row_to_dict(row) if row else None
    finally:
        db.close()


def list_users() -> list[dict]:
    db: Session = get_db()
    try:
        rows = db.query(UserRow).order_by(UserRow.created_at).all()
        return [_row_to_dict(r) for r in rows]
    finally:
        db.close()


def delete_user(user_id: str) -> bool:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.id == user_id).first()
        if row is None:
            return False
        db.delete(row)
        db.commit()
        return True
    finally:
        db.close()


def update_user(user_id: str, **fields) -> dict | None:
    """Update arbitrary columns on a user row. Returns updated user or None if not found."""
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.id == user_id).first()
        if row is None:
            return None
        for key, value in fields.items():
            if value is not None and hasattr(row, key):
                setattr(row, key, value)
        db.commit()
        db.refresh(row)
        return _row_to_dict(row)
    finally:
        db.close()


def count_users() -> int:
    db: Session = get_db()
    try:
        return db.query(UserRow).count()
    finally:
        db.close()


def set_reset_token(user_id: str, token_hash: str, expires: datetime) -> bool:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.id == user_id).first()
        if row is None:
            return False
        row.reset_token_hash = token_hash
        row.reset_token_expires = expires
        db.commit()
        return True
    finally:
        db.close()


def get_user_by_reset_token(token_hash: str) -> dict | None:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.reset_token_hash == token_hash).first()
        return _row_to_dict(row) if row else None
    finally:
        db.close()


def clear_reset_token(user_id: str) -> None:
    db: Session = get_db()
    try:
        row = db.query(UserRow).filter(UserRow.id == user_id).first()
        if row:
            row.reset_token_hash = None
            row.reset_token_expires = None
            db.commit()
    finally:
        db.close()
