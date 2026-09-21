"""Database models and ORM entities for the osu! API server.

This module exports all database models, TypedDict types, and response classes
used throughout the application for database operations and API responses.
"""

from .achievement import UserAchievement, UserAchievementResp
from .admin_audit import AdminAuditEvent
from .auth import OAuthClient, OAuthToken, TotpKeys, V1APIKeys
from .beatmap import (
    Beatmap,
    BeatmapDict,
    BeatmapModel,
)
from .beatmap_comment import BeatmapComment, BeatmapCommentVote
from .beatmap_playcounts import (
    BeatmapPlaycounts,
    BeatmapPlaycountsDict,
    BeatmapPlaycountsModel,
)
from .beatmap_ranking import (
    BeatmapRankingAudit,
    BeatmapRankingEvent,
    BeatmapRankingPolicy,
    BeatmapsetRankingPolicy,
    RankingEventType,
    RankingPolicyAction,
    RankingPolicyScope,
)
from .beatmap_sync import BeatmapSync
from .beatmap_tags import BeatmapTagVote
from .beatmapset import (
    Beatmapset,
    BeatmapsetDict,
    BeatmapsetModel,
)
from .beatmapset_ratings import BeatmapRating
from .best_scores import BestScore
from .chat import (
    ChannelType,
    ChatChannel,
    ChatChannelDict,
    ChatChannelModel,
    ChatMessage,
    ChatMessageDict,
    ChatMessageModel,
)
from .counts import (
    CountResp,
    MonthlyPlaycounts,
    ReplayWatchedCount,
)
from .daily_challenge import DailyChallengeStats, DailyChallengeStatsResp
from .events import Event
from .favourite_beatmapset import FavouriteBeatmapset
from .item_attempts_count import (
    ItemAttemptsCount,
    ItemAttemptsCountDict,
    ItemAttemptsCountModel,
)
from .marathon import Marathon, MarathonScore
from .matchmaking import (
    MatchmakingMapPreset,
    MatchmakingPool,
    MatchmakingPoolBeatmap,
    MatchmakingRoomEvent,
    MatchmakingUserEloHistory,
    MatchmakingUserStats,
)
from .multiplayer_event import MultiplayerEvent, MultiplayerEventResp
from .negative_pp import BeatmapMapperCredit, NegativePPRule
from .notification import Notification, UserNotification
from .password_reset import PasswordReset
from .playlist_best_score import PlaylistBestScore
from .playlists import Playlist, PlaylistDict, PlaylistModel
from .rank_history import RankHistory, RankHistoryResp, RankTop
from .ranked_dodge import RankedDodgePenalty
from .relationship import Relationship, RelationshipDict, RelationshipModel, RelationshipType
from .room import APIUploadedRoom, Room, RoomDict, RoomModel
from .room_participated_user import RoomParticipatedUser
from .score import (
    MultiplayerScores,
    Score,
    ScoreAround,
    ScoreDict,
    ScoreModel,
    ScoreStatistics,
)
from .score_import import ScoreImport
from .score_token import ScoreToken, ScoreTokenResp
from .screenshots import Screenshot
from .search_beatmapset import SearchBeatmapsetsResp
from .soms_activity import SomsActivity
from .somsai import (
    SomsaiActivity,
    SomsaiLock,
    SomsaiMatch,
    SomsaiNativeRoom,
    SomsaiParty,
    SomsaiPartyInvite,
    SomsaiQueue,
    SomsaiRating,
    SomsaiReservation,
)
from .somsai_map import SomsaiMap
from .somsai_pool import SomsaiPool
from .statistics import (
    UserStatistics,
    UserStatisticsDict,
    UserStatisticsModel,
)
from .team import Team, TeamMember, TeamRequest, TeamResp
from .total_score_best_scores import TotalScoreBestScore
from .user import (
    User,
    UserDict,
    UserModel,
)
from .user_account_history import (
    UserAccountHistory,
    UserAccountHistoryResp,
    UserAccountHistoryType,
)
from .user_login_log import UserLoginLog
from .user_preference import UserPreference
from .user_recovery_word import UserRecoveryWord
from .verification import EmailVerification, LoginSession, LoginSessionResp, TrustedDevice, TrustedDeviceResp

__all__ = [
    "APIUploadedRoom",
    "AdminAuditEvent",
    "Beatmap",
    "BeatmapComment",
    "BeatmapCommentVote",
    "BeatmapDict",
    "BeatmapMapperCredit",
    "BeatmapModel",
    "BeatmapPlaycounts",
    "BeatmapPlaycountsDict",
    "BeatmapPlaycountsModel",
    "BeatmapRankingAudit",
    "BeatmapRankingEvent",
    "BeatmapRankingPolicy",
    "BeatmapRating",
    "BeatmapSync",
    "BeatmapTagVote",
    "Beatmapset",
    "BeatmapsetDict",
    "BeatmapsetModel",
    "BeatmapsetRankingPolicy",
    "BestScore",
    "ChannelType",
    "ChatChannel",
    "ChatChannelDict",
    "ChatChannelModel",
    "ChatMessage",
    "ChatMessageDict",
    "ChatMessageModel",
    "CountResp",
    "DailyChallengeStats",
    "DailyChallengeStatsResp",
    "EmailVerification",
    "Event",
    "FavouriteBeatmapset",
    "ItemAttemptsCount",
    "ItemAttemptsCountDict",
    "ItemAttemptsCountModel",
    "LoginSession",
    "LoginSessionResp",
    "Marathon",
    "MarathonScore",
    "MatchmakingMapPreset",
    "MatchmakingPool",
    "MatchmakingPoolBeatmap",
    "MatchmakingRoomEvent",
    "MatchmakingUserEloHistory",
    "MatchmakingUserStats",
    "MonthlyPlaycounts",
    "MultiplayerEvent",
    "MultiplayerEventResp",
    "MultiplayerScores",
    "NegativePPRule",
    "Notification",
    "OAuthClient",
    "OAuthToken",
    "PasswordReset",
    "Playlist",
    "PlaylistBestScore",
    "PlaylistDict",
    "PlaylistModel",
    "RankHistory",
    "RankHistoryResp",
    "RankTop",
    "RankedDodgePenalty",
    "RankingEventType",
    "RankingPolicyAction",
    "RankingPolicyScope",
    "Relationship",
    "RelationshipDict",
    "RelationshipModel",
    "RelationshipType",
    "ReplayWatchedCount",
    "Room",
    "RoomDict",
    "RoomModel",
    "RoomParticipatedUser",
    "Score",
    "ScoreAround",
    "ScoreDict",
    "ScoreImport",
    "ScoreModel",
    "ScoreStatistics",
    "ScoreToken",
    "ScoreTokenResp",
    "Screenshot",
    "SearchBeatmapsetsResp",
    "SomsActivity",
    "SomsaiActivity",
    "SomsaiLock",
    "SomsaiMap",
    "SomsaiMatch",
    "SomsaiNativeRoom",
    "SomsaiParty",
    "SomsaiPartyInvite",
    "SomsaiPool",
    "SomsaiQueue",
    "SomsaiRating",
    "SomsaiReservation",
    "Team",
    "TeamMember",
    "TeamRequest",
    "TeamResp",
    "TotalScoreBestScore",
    "TotpKeys",
    "TrustedDevice",
    "TrustedDeviceResp",
    "User",
    "UserAccountHistory",
    "UserAccountHistoryResp",
    "UserAccountHistoryType",
    "UserAchievement",
    "UserAchievementResp",
    "UserDict",
    "UserLoginLog",
    "UserModel",
    "UserNotification",
    "UserPreference",
    "UserRecoveryWord",
    "UserStatistics",
    "UserStatisticsDict",
    "UserStatisticsModel",
    "V1APIKeys",
]

for i in __all__:
    if i.endswith("Model") or i.endswith("Resp"):
        globals()[i].model_rebuild()
