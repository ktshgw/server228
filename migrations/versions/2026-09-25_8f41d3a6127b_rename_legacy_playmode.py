"""Rename the legacy lazer playmode preference to the SOMS-owned field.

Revision ID: 8f41d3a6127b
Revises: 4a6f82d951c0
Create Date: 2026-09-25
"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "8f41d3a6127b"
down_revision: str | None = "4a6f82d951c0"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE = "lazer_users"
_LEGACY_COLUMN = "g0v0_playmode"
_SOMS_COLUMN = "soms_playmode"


def _columns() -> dict[str, dict]:
    return {column["name"]: column for column in sa.inspect(op.get_bind()).get_columns(_TABLE)}


def _rename(source: str, target: str) -> None:
    columns = _columns()
    if target in columns or source not in columns:
        return
    column = columns[source]
    op.alter_column(
        _TABLE,
        source,
        new_column_name=target,
        existing_type=column["type"],
        existing_nullable=column["nullable"],
        existing_server_default=column.get("default"),
    )


def upgrade() -> None:
    _rename(_LEGACY_COLUMN, _SOMS_COLUMN)


def downgrade() -> None:
    _rename(_SOMS_COLUMN, _LEGACY_COLUMN)
