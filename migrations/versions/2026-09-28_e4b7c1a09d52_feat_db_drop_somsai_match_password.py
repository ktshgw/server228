"""Remove the obsolete SOMSAI room-password storage.

Revision ID: e4b7c1a09d52
Revises: 9a3e11f420bc
Create Date: 2026-09-28
"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "e4b7c1a09d52"
down_revision: str | None = "9a3e11f420bc"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # Existing SOMSAI rooms may still contain generated values from the old
    # implementation. Privacy now comes from the authoritative SOMSAI roster.
    op.execute(sa.text("UPDATE rooms SET password = NULL WHERE name LIKE 'SOMSAI %'"))

    columns = {column["name"] for column in sa.inspect(op.get_bind()).get_columns("somsai_matches")}
    if "password" in columns:
        op.drop_column("somsai_matches", "password")


def downgrade() -> None:
    # The removed values were random transport secrets and must not be restored.
    pass
