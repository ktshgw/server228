"""Local beatmap comments and votes; reviewed autogenerate output.

Revision ID: eeedb0b27cc7
Revises: 1e3a16f7831f
"""

from alembic import op
import sqlalchemy as sa

revision = "eeedb0b27cc7"
down_revision = "1e3a16f7831f"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "web_beatmap_comments",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("body", sa.Text(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_web_beatmap_comments_set_created", "web_beatmap_comments", ["beatmapset_id", "created_at", "id"]
    )
    op.create_index("ix_web_beatmap_comments_user_id", "web_beatmap_comments", ["user_id"])
    op.create_table(
        "web_beatmap_comment_votes",
        sa.Column(
            "comment_id", sa.Integer(), sa.ForeignKey("web_beatmap_comments.id", ondelete="CASCADE"), primary_key=True
        ),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), primary_key=True),
    )


def downgrade() -> None:
    op.drop_table("web_beatmap_comment_votes")
    op.drop_table("web_beatmap_comments")
