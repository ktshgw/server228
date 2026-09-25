"""Signed weighted PP: raw score PP and best-score ordering remain unchanged."""

from urllib.parse import quote, urlparse

from app.database.beatmap import Beatmap
from app.database.best_scores import BestScore
from app.database.negative_pp import BeatmapMapperCredit, NegativePPRule

from fastapi import HTTPException
from httpx import HTTPError
from sqlalchemy import and_, or_
from sqlmodel import col, exists, func, select


def target_maps(kind: str, target_id: int):
    if kind == "beatmap":
        condition = col(Beatmap.id) == target_id
    elif kind == "beatmapset":
        condition = col(Beatmap.beatmapset_id) == target_id
    elif kind == "mapper":
        condition = exists().where(
            col(BeatmapMapperCredit.beatmap_id) == col(Beatmap.id),
            col(BeatmapMapperCredit.mapper_id) == target_id,
        )
    else:
        raise ValueError("Unknown negative PP rule kind")
    return select(Beatmap.id).where(condition)


def penalized_maps():
    mapper_rule = select(BeatmapMapperCredit.beatmap_id).join(
        NegativePPRule,
        and_(
            col(NegativePPRule.kind) == "mapper",
            col(NegativePPRule.target_id) == col(BeatmapMapperCredit.mapper_id),
        ),
    )
    return select(Beatmap.id).where(
        or_(
            exists().where(
                col(NegativePPRule.kind) == "beatmap",
                col(NegativePPRule.target_id) == col(Beatmap.id),
            ),
            exists().where(
                col(NegativePPRule.kind) == "beatmapset",
                col(NegativePPRule.target_id) == col(Beatmap.beatmapset_id),
            ),
            col(Beatmap.id).in_(mapper_rule),
        )
    )


async def negative_map_ids(session, beatmap_ids):
    if not beatmap_ids:
        return set()
    # Locking reads see rules committed while this transaction waited on a
    # statistics/beatmapset lock (MySQL otherwise retains its earlier snapshot).
    rules = (await session.exec(select(NegativePPRule).with_for_update(read=True))).all()
    if not rules:
        return set()
    maps = (await session.exec(select(Beatmap.id, Beatmap.beatmapset_id).where(col(Beatmap.id).in_(beatmap_ids)))).all()
    singles = {rule.target_id for rule in rules if rule.kind == "beatmap"}
    sets = {rule.target_id for rule in rules if rule.kind == "beatmapset"}
    mappers = {rule.target_id for rule in rules if rule.kind == "mapper"}
    result = {bid for bid, sid in maps if bid in singles or sid in sets}
    if mappers:
        result.update(
            (
                await session.exec(
                    select(BeatmapMapperCredit.beatmap_id).where(
                        col(BeatmapMapperCredit.beatmap_id).in_(beatmap_ids),
                        col(BeatmapMapperCredit.mapper_id).in_(mappers),
                    )
                )
            ).all()
        )
    return result


async def negative_score_counts(session) -> dict[int, int]:
    """Count saved eligible score IDs across all modes, without a best-score limit.

    The map subquery tests membership rather than joining rules: a mapper/set/
    difficulty overlap still counts each play once. Replays of the same map
    have separate score IDs and therefore count as separate plays.
    """
    from app.database.score import Score

    cache_key = "negative_pp_score_counts"
    if cache_key not in session.info:
        session.info[cache_key] = dict(
            (
                await session.exec(
                    select(Score.user_id, func.count(col(Score.id)))
                    .where(
                        col(Score.beatmap_id).in_(penalized_maps()),
                        col(Score.pp) > 0,
                        col(Score.ranked).is_(True),
                        col(Score.passed).is_(True),
                        col(Score.processed).is_(True),
                    )
                    .group_by(col(Score.user_id))
                )
            ).all()
        )
    return session.info[cache_key]


async def negative_score_users(session):
    return set(await negative_score_counts(session))


def negative_pp_title(score_count: int) -> dict | None:
    """Return only the highest currently earned title."""
    for minimum, tier, name in (
        (1000, 3, "Верховный копрогастроном"),
        (100, 2, "Сомелье дристни"),
        (1, 1, "Говноед"),
    ):
        if score_count >= minimum:
            return {"tier": tier, "name": name}
    return None


async def recalculate(session, beatmap_ids):
    """Caller owns transaction; use submission's statistics lock before reading bests."""
    from app.database.score import calculate_user_pp
    from app.database.statistics import UserStatistics
    from app.service.beatmap_ranking_reconciliation_service import ScoreReconciliationResult

    modes = (
        set(
            (
                await session.exec(
                    select(BestScore.user_id, BestScore.gamemode)
                    .where(col(BestScore.beatmap_id).in_(beatmap_ids))
                    .distinct()
                )
            ).all()
        )
        if beatmap_ids
        else set()
    )
    for user_id, mode in sorted(modes, key=lambda item: (item[0], str(item[1]))):
        statistics = (
            await session.exec(
                select(UserStatistics)
                .where(UserStatistics.user_id == user_id, UserStatistics.mode == mode)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if statistics:
            statistics.pp, statistics.hit_accuracy = await calculate_user_pp(session, user_id, mode, for_update=True)
    session.info.pop("negative_pp_score_counts", None)
    return ScoreReconciliationResult(affected_user_modes=frozenset(modes))


async def reconcile_attribution(session, beatmap_ids):
    """An upstream attribution correction must update existing weighted totals too."""
    if (await session.exec(select(NegativePPRule.id).where(NegativePPRule.kind == "mapper").limit(1))).first():
        await session.flush()
        return await recalculate(session, beatmap_ids)
    from app.service.beatmap_ranking_reconciliation_service import ScoreReconciliationResult

    return ScoreReconciliationResult()


def reference_id(kind: str, reference: str) -> str:
    reference = reference.strip()
    if "://" not in reference:
        return reference
    url = urlparse(reference)
    if url.scheme != "https" or url.hostname != "osu.ppy.sh":
        raise HTTPException(422, "Укажите ID или ссылку на osu.ppy.sh")
    path = url.path.strip("/").split("/")
    if kind == "mapper" and len(path) == 2 and path[0] in {"users", "u"}:
        return path[1]
    if kind == "beatmap" and path[0] == "beatmapsets" and "/" in url.fragment:
        return url.fragment.rsplit("/", 1)[1]
    prefixes = {"beatmap": {"beatmaps", "b"}, "beatmapset": {"beatmapsets", "s"}}
    if len(path) == 2 and path[0] in prefixes.get(kind, set()):
        return path[1]
    raise HTTPException(422, "Ссылка не соответствует выбранному типу правила")


async def resolve_target(session, fetcher, kind: str, reference: str):
    from app.database.beatmapset import Beatmapset

    value = reference_id(kind, reference)
    if kind == "mapper":
        data = await fetcher.request_api(
            f"https://osu.ppy.sh/api/v2/users/{quote(value, safe='')}",
            params={"key": "id" if value.isdecimal() else "username"},
        )
        return int(data["id"]), str(data["username"])
    if not value.isdecimal() or not 0 < int(value) < 2**31:
        raise HTTPException(422, "Нужен положительный ID карты или сложности")
    target_id = int(value)
    if kind == "beatmap":
        beatmap = await Beatmap.get_or_fetch(session, fetcher, bid=target_id)
        return target_id, f"{beatmap.beatmapset.artist} — {beatmap.beatmapset.title} [{beatmap.version}]"[:300]
    beatmapset = await Beatmapset.get_or_fetch(session, fetcher, target_id)
    return target_id, f"{beatmapset.artist} — {beatmapset.title}"[:300]


async def refresh_owners(session, fetcher, beatmap_ids):
    """Fetch only authoritative owner IDs; never infer guests from a title or set host."""
    from app.service.beatmap_ranking_reconciliation_service import invalidate_score_reconciliation_caches

    set_ids = (
        (
            await session.exec(
                select(Beatmap.beatmapset_id)
                .where(col(Beatmap.id).in_(beatmap_ids), col(Beatmap.owners_known).is_(False))
                .distinct()
            )
        ).all()
        if beatmap_ids
        else []
    )
    for set_id in set_ids:
        try:
            data = await fetcher.request_api(f"https://osu.ppy.sh/api/v2/beatmapsets/{set_id}")
        except HTTPError as exc:
            raise HTTPException(
                502, f"Не удалось обновить авторство мапсета {set_id} из osu! API. Повторите позже."
            ) from exc
        snapshots = {int(item["id"]): item for item in data.get("beatmaps", [])}
        # Parent lock matches submission/update lock order. Network I/O above is outside it.
        from app.database.beatmapset import Beatmapset

        await session.exec(select(Beatmapset.id).where(Beatmapset.id == set_id).with_for_update())
        maps = (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == set_id).with_for_update())).all()
        changed = []
        for beatmap in maps:
            snapshot = snapshots.get(beatmap.id)
            if snapshot is None or not isinstance(snapshot.get("owners"), list):
                continue
            await session.refresh(beatmap, attribute_names=["mapper_credits"])
            owners = {int(owner["id"]): str(owner["username"]) for owner in snapshot["owners"]}
            beatmap.mapper_credits = [
                BeatmapMapperCredit(beatmap_id=beatmap.id, mapper_id=uid, username=name) for uid, name in owners.items()
            ]
            beatmap.owners_known = True
            changed.append(beatmap.id)
        result = await reconcile_attribution(session, changed)
        await session.commit()
        await invalidate_score_reconciliation_caches(result)
    missing = (
        (
            await session.exec(
                select(Beatmap.id)
                .where(col(Beatmap.id).in_(beatmap_ids), col(Beatmap.owners_known).is_(False))
                .limit(1)
            )
        ).first()
        if beatmap_ids
        else None
    )
    if missing:
        raise HTTPException(
            409, f"Не удалось получить авторов сложности {missing}. Повторите позже или добавьте её по ID."
        )


async def prepare_mapper_rule(session, fetcher):
    from app.database.score import Score

    # Include non-best scores so the badge also works for historical guest scores.
    ids = (await session.exec(select(Score.beatmap_id).where(Score.pp > 0, Score.ranked == True).distinct())).all()  # noqa: E712
    await refresh_owners(session, fetcher, ids)


def rule_payload(rule):
    return rule.model_dump(mode="json")
