from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import Relationship, RelationshipType
from app.router.private.web_site import _web_friendship_payload, update_web_friendship

from fastapi import Response


class WebFriendshipTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def _context(user_id: int = 7):
        return SimpleNamespace(
            user=SimpleNamespace(
                id=user_id,
                is_restricted=AsyncMock(return_value=False),
            )
        )

    async def test_payload_distinguishes_one_way_and_mutual_friendship(self) -> None:
        viewer_follow = SimpleNamespace(user_id=7, target_id=9, type=RelationshipType.FOLLOW)
        session = SimpleNamespace(
            exec=AsyncMock(
                side_effect=[
                    SimpleNamespace(one=lambda: 12),
                    SimpleNamespace(all=lambda: [viewer_follow]),
                ]
            )
        )

        one_way = await _web_friendship_payload(
            session,  # type: ignore[arg-type]
            viewer_user_id=7,
            target_user_id=9,
        )

        assert one_way == {
            "target_user_id": 9,
            "follower_count": 12,
            "is_following": True,
            "is_followed": False,
            "mutual": False,
        }

        target_follow = SimpleNamespace(user_id=9, target_id=7, type=RelationshipType.FOLLOW)
        session.exec = AsyncMock(
            side_effect=[
                SimpleNamespace(one=lambda: 13),
                SimpleNamespace(all=lambda: [viewer_follow, target_follow]),
            ]
        )
        mutual = await _web_friendship_payload(
            session,  # type: ignore[arg-type]
            viewer_user_id=7,
            target_user_id=9,
        )

        assert mutual["is_following"] is True
        assert mutual["is_followed"] is True
        assert mutual["mutual"] is True
        assert mutual["follower_count"] == 13

    async def test_put_adds_follow_and_returns_fresh_state(self) -> None:
        session = SimpleNamespace(
            exec=AsyncMock(
                side_effect=[
                    SimpleNamespace(first=lambda: 7),
                    SimpleNamespace(all=lambda: []),
                    SimpleNamespace(first=lambda: SimpleNamespace(id=17)),
                ]
            ),
            add=Mock(),
            flush=AsyncMock(),
            delete=AsyncMock(),
            commit=AsyncMock(),
        )
        response = Response()
        expected = {
            "target_user_id": 9,
            "follower_count": 1,
            "is_following": True,
            "is_followed": False,
            "mutual": False,
        }
        with (
            patch("app.router.private.web_site._require_csrf") as require_csrf,
            patch(
                "app.router.private.web_site._find_public_user",
                new=AsyncMock(return_value=SimpleNamespace(id=9)),
            ),
            patch("app.router.private.web_site._web_friendship_payload", new=AsyncMock(return_value=expected)),
            patch("app.router.private.web_site._emit_profile_event") as emit,
            patch("app.features.somsai.services.soms_activity_service.stage_friend_notification", new=AsyncMock()) as notify,
        ):
            result = await update_web_friendship(
                "9",
                SimpleNamespace(method="PUT"),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                response,
            )

        require_csrf.assert_called_once()
        notify.assert_awaited_once()
        added = session.add.call_args.args[0]
        assert isinstance(added, Relationship)
        assert added.user_id == 7
        assert added.target_id == 9
        assert added.type == RelationshipType.FOLLOW
        session.commit.assert_awaited_once()
        emitted = emit.call_args.args[0]
        assert emitted.user_id == 7
        assert emitted.target_user_id == 9
        assert emitted.relationship_type == "friend"
        assert emitted.action == "add"
        assert result == expected
        assert response.headers["cache-control"] == "private, no-store"

    async def test_delete_removes_follow_without_touching_other_relationship_types(self) -> None:
        follow = Relationship(id=1, user_id=7, target_id=9, type=RelationshipType.FOLLOW)
        block = Relationship(id=2, user_id=7, target_id=9, type=RelationshipType.BLOCK)
        session = SimpleNamespace(
            exec=AsyncMock(
                side_effect=[
                    SimpleNamespace(first=lambda: 7),
                    SimpleNamespace(all=lambda: [follow, block]),
                ]
            ),
            add=Mock(),
            delete=AsyncMock(),
            commit=AsyncMock(),
        )
        expected = {
            "target_user_id": 9,
            "follower_count": 0,
            "is_following": False,
            "is_followed": False,
            "mutual": False,
        }
        with (
            patch("app.router.private.web_site._require_csrf"),
            patch(
                "app.router.private.web_site._find_public_user",
                new=AsyncMock(return_value=SimpleNamespace(id=9)),
            ),
            patch("app.router.private.web_site._web_friendship_payload", new=AsyncMock(return_value=expected)),
            patch("app.router.private.web_site._emit_profile_event") as emit,
            patch("app.features.somsai.services.soms_activity_service.stage_friend_notification", new=AsyncMock()) as notify,
        ):
            result = await update_web_friendship(
                "9",
                SimpleNamespace(method="DELETE"),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                Response(),
            )

        session.delete.assert_awaited_once_with(follow)
        notify.assert_awaited_once()
        assert notify.call_args.kwargs == {"removed": True}
        session.commit.assert_awaited_once()
        assert emit.call_args.args[0].action == "delete"
        assert result == expected


if __name__ == "__main__":
    unittest.main()
