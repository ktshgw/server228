"""feat: seed independent Ranked 2v2 queues for each supported ruleset"""

from alembic import op
import sqlalchemy as sa

revision = "bc803fd64e29"
down_revision = "a472c8f019d3"
branch_labels = None
depends_on = None


def upgrade() -> None:
    # Existing one-player queues and operator presets are preserved. New team
    # pools use the same live catalogue with independent pool-keyed player Elo.
    connection = op.get_bind()
    for ruleset_id, variant in ((0, 0), (1, 0), (2, 0), (3, 4), (3, 7)):
        connection.execute(
            sa.text(
                "INSERT INTO matchmaking_pools "
                "(ruleset_id, variant_id, name, type, ranked, active, lobby_size, "
                "rating_search_radius, rating_search_radius_max, rating_search_radius_exp, use_dmr) "
                "VALUES (:ruleset, :variant, 'SOMS! 2v2', 'ranked_play', 1, 1, 4, 150, 9999, 15, 0) "
                "ON DUPLICATE KEY UPDATE id = matchmaking_pools.id"
            ),
            {"ruleset": ruleset_id, "variant": variant},
        )


def downgrade() -> None:
    # Preserve played matches and Elo, while removing these queues from search.
    op.execute("UPDATE matchmaking_pools SET active = 0 WHERE name = 'SOMS! 2v2' AND type = 'ranked_play'")
