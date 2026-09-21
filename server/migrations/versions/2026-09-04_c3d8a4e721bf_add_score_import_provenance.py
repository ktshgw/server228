"""feat(db): add score import provenance

Revision ID: c3d8a4e721bf
Revises: 9f31c6e2a741
Create Date: 2026-09-04 18:30:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "c3d8a4e721bf"
down_revision: str | Sequence[str] | None = "9f31c6e2a741"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "score_imports",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("score_id", sa.BigInteger(), nullable=True),
        sa.Column("target_user_id", sa.Integer(), nullable=False),
        sa.Column("imported_by_user_id", sa.Integer(), nullable=True),
        sa.Column("source", sa.String(length=32), nullable=False),
        sa.Column("source_fingerprint", sa.String(length=96), nullable=False),
        sa.Column("source_ruleset", sa.String(length=16), nullable=False),
        sa.Column("source_score_id", sa.String(length=64), nullable=False),
        sa.Column("source_user_id", sa.BigInteger(), nullable=True),
        sa.Column("source_username", sa.String(length=32), nullable=True),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("replay_imported", sa.Boolean(), nullable=False),
        sa.Column("source_snapshot", sa.JSON(), nullable=False),
        sa.Column("imported_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["imported_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.ForeignKeyConstraint(["score_id"], ["scores.id"], ondelete="SET NULL"),
        sa.ForeignKeyConstraint(["target_user_id"], ["lazer_users.id"], ondelete="RESTRICT"),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("source_fingerprint", name="uq_score_import_source_fingerprint"),
        sa.UniqueConstraint("score_id"),
    )
    op.create_index(op.f("ix_score_imports_score_id"), "score_imports", ["score_id"])
    op.create_index(op.f("ix_score_imports_target_user_id"), "score_imports", ["target_user_id"])
    op.create_index(op.f("ix_score_imports_imported_by_user_id"), "score_imports", ["imported_by_user_id"])
    op.create_index(op.f("ix_score_imports_source_user_id"), "score_imports", ["source_user_id"])
    op.create_index(op.f("ix_score_imports_imported_at"), "score_imports", ["imported_at"])
    op.create_index(
        "ix_score_imports_source_score",
        "score_imports",
        ["source", "source_ruleset", "source_score_id"],
    )


def downgrade() -> None:
    op.drop_index("ix_score_imports_source_score", table_name="score_imports")
    op.drop_index(op.f("ix_score_imports_imported_at"), table_name="score_imports")
    op.drop_index(op.f("ix_score_imports_source_user_id"), table_name="score_imports")
    op.drop_index(op.f("ix_score_imports_imported_by_user_id"), table_name="score_imports")
    op.drop_index(op.f("ix_score_imports_target_user_id"), table_name="score_imports")
    op.drop_index(op.f("ix_score_imports_score_id"), table_name="score_imports")
    op.drop_table("score_imports")
