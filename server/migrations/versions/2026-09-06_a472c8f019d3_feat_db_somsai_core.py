"""feat(db): SOMSAI parties, leases, matches and independent ratings

Revision ID: a472c8f019d3
Revises: 9f31a7c2d804
"""

from alembic import op
import sqlalchemy as sa

revision = "a472c8f019d3"
down_revision = "9f31a7c2d804"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("rooms", sa.Column("tournament_mode", sa.Boolean(), nullable=False, server_default=sa.false()))
    op.create_table("somsai_lock", sa.Column("id", sa.Integer(), primary_key=True))
    op.execute(sa.text("INSERT INTO somsai_lock (id) VALUES (1)"))
    op.create_table(
        "somsai_native_rooms",
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), primary_key=True),
        sa.Column("room_id", sa.Integer(), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_table(
        "somsai_parties",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("captain_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("members", sa.JSON(), nullable=False),
        sa.Column("active", sa.Boolean(), nullable=False),
        sa.Column("revision", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
    )
    op.create_table(
        "somsai_party_invites",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("party_id", sa.Integer(), sa.ForeignKey("somsai_parties.id"), nullable=False),
        sa.Column("inviter_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("target_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("status", sa.String(16), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_somsai_party_invites_target_id", "somsai_party_invites", ["target_id"])
    op.create_table(
        "somsai_activity",
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), primary_key=True),
        sa.Column("party_id", sa.Integer(), sa.ForeignKey("somsai_parties.id"), nullable=True),
        sa.Column("reservation_id", sa.String(36), nullable=True),
        sa.Column("match_id", sa.Integer(), nullable=True),
    )
    op.create_index("ix_somsai_activity_reservation_id", "somsai_activity", ["reservation_id"])
    op.create_index("ix_somsai_activity_match_id", "somsai_activity", ["match_id"])
    op.create_table(
        "somsai_reservations",
        sa.Column("id", sa.String(36), primary_key=True),
        sa.Column("captain_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("party_id", sa.Integer(), nullable=True),
        sa.Column("kind", sa.String(32), nullable=False),
        sa.Column("members", sa.JSON(), nullable=False),
        sa.Column("released", sa.Boolean(), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_somsai_reservations_expires_at", "somsai_reservations", ["expires_at"])
    op.create_table(
        "somsai_ratings",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("format", sa.String(8), nullable=False),
        sa.Column("rating", sa.Float(), nullable=False),
        sa.Column("initial_rating", sa.Float(), nullable=False),
        sa.Column("initial_rank", sa.Integer(), nullable=True),
        sa.Column("initial_population", sa.Integer(), nullable=False),
        sa.Column("wins", sa.Integer(), nullable=False),
        sa.Column("losses", sa.Integer(), nullable=False),
        sa.Column("draws", sa.Integer(), nullable=False),
        sa.Column("games", sa.Integer(), nullable=False),
        sa.Column("last_delta", sa.Float(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.UniqueConstraint("user_id", "ruleset_id", "variant_id", "format", name="uq_somsai_rating"),
    )
    op.create_index("ix_somsai_ratings_user_id", "somsai_ratings", ["user_id"])
    op.create_table(
        "somsai_queue",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("reservation_id", sa.String(36), nullable=False, unique=True),
        sa.Column("captain_id", sa.Integer(), nullable=False),
        sa.Column("members", sa.JSON(), nullable=False),
        sa.Column("format", sa.String(8), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("rating", sa.Float(), nullable=False),
        sa.Column("joined_at", sa.DateTime(), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_somsai_queue_format", "somsai_queue", ["ruleset_id", "variant_id", "format"])
    op.create_table(
        "somsai_matches",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("name", sa.String(100), nullable=False),
        sa.Column("format", sa.String(8), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("ranked", sa.Boolean(), nullable=False),
        sa.Column("owner_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("pool_id", sa.Integer(), nullable=False),
        sa.Column("room_id", sa.Integer(), nullable=True),
        sa.Column("password", sa.String(64), nullable=False),
        sa.Column("stage", sa.String(16), nullable=False),
        sa.Column("revision", sa.Integer(), nullable=False),
        sa.Column("state", sa.JSON(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("ended_at", sa.DateTime(), nullable=True),
    )
    op.create_index("ix_somsai_matches_room_id", "somsai_matches", ["room_id"])
    op.create_index("ix_somsai_matches_stage", "somsai_matches", ["stage"])


def downgrade() -> None:
    for table in (
        "somsai_matches",
        "somsai_queue",
        "somsai_ratings",
        "somsai_reservations",
        "somsai_activity",
        "somsai_party_invites",
        "somsai_parties",
        "somsai_native_rooms",
        "somsai_lock",
    ):
        op.drop_table(table)
    op.drop_column("rooms", "tournament_mode")
