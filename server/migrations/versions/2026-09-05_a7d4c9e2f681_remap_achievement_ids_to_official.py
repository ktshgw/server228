"""remap achievement ids to their official osu! values

Revision ID: a7d4c9e2f681
Revises: c6e8d0a4f125
Create Date: 2026-09-05 01:10:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "a7d4c9e2f681"
down_revision: str | Sequence[str] | None = "c6e8d0a4f125"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

# The original server catalogue assigned sequential private IDs.  This is a
# bijection from those IDs to the official achievement IDs, matched by the
# unique asset slug in the official 2026-09-05 catalogue.
LEGACY_TO_OFFICIAL: tuple[tuple[int, int], ...] = (
    (1, 55),
    (2, 56),
    (3, 57),
    (4, 58),
    (5, 59),
    (6, 60),
    (7, 61),
    (8, 62),
    (9, 242),
    (10, 244),
    (11, 63),
    (12, 64),
    (13, 65),
    (14, 66),
    (15, 67),
    (16, 68),
    (17, 69),
    (18, 70),
    (19, 243),
    (20, 245),
    (21, 1),
    (22, 3),
    (23, 4),
    (24, 5),
    (25, 71),
    (26, 72),
    (27, 73),
    (28, 74),
    (29, 75),
    (30, 76),
    (31, 77),
    (32, 78),
    (33, 95),
    (34, 96),
    (35, 97),
    (36, 98),
    (37, 99),
    (38, 100),
    (39, 101),
    (40, 102),
    (41, 79),
    (42, 80),
    (43, 81),
    (44, 82),
    (45, 83),
    (46, 84),
    (47, 85),
    (48, 86),
    (49, 103),
    (50, 104),
    (51, 105),
    (52, 106),
    (53, 107),
    (54, 108),
    (55, 109),
    (56, 110),
    (57, 87),
    (58, 88),
    (59, 89),
    (60, 90),
    (61, 91),
    (62, 92),
    (63, 93),
    (64, 94),
    (65, 111),
    (66, 112),
    (67, 113),
    (68, 114),
    (69, 115),
    (70, 116),
    (71, 117),
    (72, 118),
    (73, 20),
    (74, 21),
    (75, 22),
    (76, 28),
    (77, 31),
    (78, 32),
    (79, 33),
    (80, 291),
    (81, 13),
    (82, 23),
    (83, 24),
    (84, 292),
    (85, 46),
    (86, 47),
    (87, 48),
    (88, 293),
    (89, 119),
    (90, 120),
    (91, 121),
    (92, 122),
    (93, 123),
    (94, 124),
    (95, 125),
    (96, 126),
    (97, 127),
    (98, 128),
    (99, 131),
    (100, 339),
    (101, 340),
    (102, 336),
    (103, 337),
    (104, 338),
    (105, 41),
    (106, 44),
    (107, 134),
    (108, 137),
    (109, 287),
    (110, 138),
    (111, 140),
    (112, 144),
    (113, 147),
    (114, 148),
    (115, 150),
    (116, 151),
    (117, 152),
    (118, 153),
    (119, 154),
    (120, 160),
    (121, 199),
    (122, 202),
    (123, 223),
    (124, 225),
    (127, 273),
    (128, 279),
    (129, 285),
    (130, 318),
    (131, 319),
    (132, 328),
    (133, 346),
    (134, 349),
    (353, 353),
    (354, 354),
    (355, 355),
    (356, 356),
)

STAGING_TABLE = "achievement_id_remap_staging"


def _case_expression(mapping: Sequence[tuple[int, int]]) -> str:
    clauses = " ".join(f"WHEN {source} THEN {target}" for source, target in mapping)
    return f"CASE achievement_id {clauses} ELSE achievement_id END"


def _remap(mapping: Sequence[tuple[int, int]]) -> None:
    """Remap through a staging table so cycles never hit the unique index."""

    op.execute(sa.text(f"DROP TABLE IF EXISTS {STAGING_TABLE}"))
    create_staging_sql = (
        f"CREATE TEMPORARY TABLE {STAGING_TABLE} AS "  # noqa: S608 - identifier is a migration constant
        "SELECT id, user_id, achievement_id, achieved_at FROM lazer_user_achievements"
    )
    op.execute(sa.text(create_staging_sql))
    remapped_id = _case_expression(mapping)
    try:
        op.execute(sa.text("DELETE FROM lazer_user_achievements"))
        insert_remapped_sql = (
            "INSERT INTO lazer_user_achievements (id, user_id, achievement_id, achieved_at) "  # noqa: S608
            "SELECT MIN(id), user_id, mapped_id, MIN(achieved_at) "
            "FROM ("
            f"SELECT id, user_id, {remapped_id} AS mapped_id, achieved_at FROM {STAGING_TABLE}"
            ") AS remapped "
            "GROUP BY user_id, mapped_id"
        )
        op.execute(sa.text(insert_remapped_sql))
    finally:
        op.execute(sa.text(f"DROP TABLE IF EXISTS {STAGING_TABLE}"))


def upgrade() -> None:
    _remap(LEGACY_TO_OFFICIAL)


def downgrade() -> None:
    _remap(tuple((official, legacy) for legacy, official in LEGACY_TO_OFFICIAL))
