// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using MySqlConnector;
using osu.Game.Online.Multiplayer;
using osu.Game.Scoring;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Database
{
  public class DatabaseAccess : IDatabaseAccess
  {
    private MySqlConnection? openConnection;
    private readonly ILogger<DatabaseAccess> logger;
    private readonly ISharedInterop sharedInterop;
    private readonly RulesetManager manager;

    public DatabaseAccess(ILoggerFactory loggerFactory, ISharedInterop sharedInterop, RulesetManager manager)
    {
      logger = loggerFactory.CreateLogger<DatabaseAccess>();
      this.sharedInterop = sharedInterop;
      this.manager = manager;
    }

    public async Task<int?> GetUserIdFromTokenAsync(JsonWebToken jwtToken)
    {
      var userIdClaim = jwtToken.GetClaim("sub")?.Value;
      if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
        return null;

      var connection = await getConnectionAsync();
      var result = await connection.QueryFirstOrDefaultAsync<int?>(
          "SELECT user_id FROM oauth_tokens WHERE user_id = @userId AND expires_at > UTC_TIMESTAMP()",
          new { userId = userId });
      return result;
    }

    public async Task<string?> GetUsernameAsync(int userId)
    {
      var connection = await getConnectionAsync();

      return await connection.QueryFirstOrDefaultAsync<string?>("SELECT username FROM lazer_users WHERE id = @UserID", new
      {
        UserID = userId
      });
    }

    public async Task<int[]> GetUsersInGroupsAsync(int[] groupIds)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<int>("SELECT DISTINCT `user_id` FROM `phpbb_user_group` WHERE `group_id` IN @groupIds", new
      {
        groupIds = groupIds
      })).ToArray();
    }

    public async Task<bool> IsUserRestrictedAsync(int userId)
    {
      var connection = await getConnectionAsync();

      var result = await connection.QueryFirstOrDefaultAsync<int>(@"SELECT EXISTS(
                SELECT 1
                FROM user_account_history
                WHERE user_id = @UserID
                AND type = 'restriction'
                AND (
                    permanent = TRUE
                    OR (
                        TIMESTAMPADD(SECOND, length, timestamp) > NOW()
                        AND NOW() > timestamp
                    )
                )
            ) AS is_restricted;", new { UserID = userId });
      return result == 1;
    }

    public async Task<multiplayer_room?> GetRoomAsync(long roomId)
    {
      var connection = await getConnectionAsync();

      return await connection.QueryFirstOrDefaultAsync<multiplayer_room>("SELECT * FROM rooms WHERE id = @RoomID", new
      {
        RoomID = roomId
      });
    }

    public async Task<multiplayer_room?> GetRealtimeRoomAsync(long roomId)
    {
      var connection = await getConnectionAsync();

      return await connection.QueryFirstOrDefaultAsync<multiplayer_room>("SELECT * FROM rooms WHERE type != 'multiplayer_playlist_items' AND id = @RoomID", new
      {
        RoomID = roomId
      });
    }

    public async Task<database_beatmap?> GetBeatmapAsync(int beatmapId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<database_beatmap>(
          @"SELECT
                    id as beatmap_id,
                    beatmapset_id,
                    checksum,
                    beatmap_status as approved,
                    difficulty_rating,
                    total_length,
                    CASE
                        WHEN mode = 'osu' THEN 0
                        WHEN mode = 'taiko' THEN 1
                        WHEN mode = 'fruits' THEN 2
                        WHEN mode = 'mania' THEN 3
                        WHEN mode = 'osurx' THEN 4
                        WHEN mode = 'osuap' THEN 5
                        WHEN mode = 'taikorx' THEN 6
                        WHEN mode = 'fruitsrx' THEN 7
                        ELSE 0
                    END as playmode,
                    14 as osu_file_version
                FROM beatmaps
                WHERE id = @BeatmapId AND deleted_at IS NULL",
          new { BeatmapId = beatmapId });
    }

    public async Task<database_beatmap?> GetBeatmapOrFetchAsync(int beatmapId)
    {
      var beatmap = await GetBeatmapAsync(beatmapId);
      if (beatmap != null) return beatmap;

      logger.LogDebug("Beatmap {BeatmapId} not found in database, requesting LIO to fetch it", beatmapId);

      try
      {
        await sharedInterop.EnsureBeatmapPresentAsync(beatmapId);
        logger.LogDebug("LIO returned success for beatmap {BeatmapId}, checking database again", beatmapId);
        return await GetBeatmapAsync(beatmapId);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "LIO request failed for beatmap {BeatmapId}: {ErrorMessage}", beatmapId, ex.Message);
        return null;
      }
    }

    public async Task<fail_time?> GetBeatmapFailTimeAsync(int beatmapId)
    {
      var connection = await getConnectionAsync();
      return await connection.QuerySingleOrDefaultAsync<fail_time>(
          "SELECT * FROM failtime WHERE beatmap_id = @BeatmapId",
          new { BeatmapId = beatmapId });
    }

    public async Task UpdateFailTimeAsync(fail_time failTime)
    {
      var connection = await getConnectionAsync();
      await connection.ExecuteAsync(
          @"INSERT INTO failtime (beatmap_id, fail, `exit`)
                VALUES (@BeatmapId, @Fail, @Exit)
                ON DUPLICATE KEY UPDATE fail = @Fail, `exit` = @Exit",
          new { BeatmapId = failTime.beatmap_id, Fail = failTime.fail, Exit = failTime.exit });
    }

    public async Task<int?> GetUserPlaytimeAsync(string gamemode, int userId)
    {
      var connection = await getConnectionAsync();
      return await connection.QuerySingleOrDefaultAsync<int?>(
          "SELECT play_time FROM lazer_user_statistics WHERE user_id = @UserId AND mode = @GameMode",
          new { UserId = userId, GameMode = gamemode });
    }

    public async Task UpdateUserPlaytimeAsync(string gamemode, int userId, int playTime)
    {
      var connection = await getConnectionAsync();
      await connection.ExecuteAsync(
          "UPDATE lazer_user_statistics SET play_time = @PlayTime WHERE user_id = @UserId AND mode = @GameMode",
          new { UserId = userId, GameMode = gamemode, PlayTime = playTime });
    }

    public async Task<database_beatmap[]> GetBeatmapsAsync(int[] beatmapIds)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<database_beatmap>(
          @"SELECT
                    id as beatmap_id,
                    beatmapset_id,
                    checksum,
                    beatmap_status as approved,
                    difficulty_rating,
                    total_length,
                    CASE
                        WHEN mode = 'osu' THEN 0
                        WHEN mode = 'taiko' THEN 1
                        WHEN mode = 'fruits' THEN 2
                        WHEN mode = 'mania' THEN 3
                        WHEN mode = 'osurx' THEN 4
                        WHEN mode = 'osuap' THEN 5
                        WHEN mode = 'taikorx' THEN 6
                        WHEN mode = 'fruitsrx' THEN 7
                        ELSE 0
                    END as playmode,
                    14 as osu_file_version
                FROM beatmaps
                WHERE id IN @BeatmapIds AND deleted_at IS NULL",
          new { BeatmapIds = beatmapIds })).ToArray();
    }

    public async Task<database_beatmap[]> GetBeatmapsAsync(int beatmapSetId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<database_beatmap>(
          @"SELECT
                    id as beatmap_id,
                    beatmapset_id,
                    checksum,
                    beatmap_status as approved,
                    difficulty_rating,
                    total_length,
                    CASE
                        WHEN mode = 'osu' THEN 0
                        WHEN mode = 'taiko' THEN 1
                        WHEN mode = 'fruits' THEN 2
                        WHEN mode = 'mania' THEN 3
                        WHEN mode = 'osurx' THEN 4
                        WHEN mode = 'osuap' THEN 5
                        WHEN mode = 'taikorx' THEN 6
                        WHEN mode = 'fruitsrx' THEN 7
                        ELSE 0
                    END as playmode,
                    14 as osu_file_version
                FROM beatmaps
                WHERE beatmapset_id = @BeatmapSetId AND deleted_at IS NULL",
          new { BeatmapSetId = beatmapSetId })).ToArray();
    }

    public async Task SetRoomEndDateAsync(MultiplayerRoom room, DateTimeOffset? endDate)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("UPDATE rooms SET ends_at = @EndDate WHERE id = @RoomID", new
      {
        RoomID = room.RoomID,
        EndDate = endDate
      });
    }

    public async Task UpdateRoomSettingsAsync(MultiplayerRoom room)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("UPDATE rooms SET name = @Name, password = @Password, type = @MatchType, queue_mode = @QueueMode WHERE id = @RoomID", new
      {
        RoomID = room.RoomID,
        Name = room.Settings.Name,
        Password = room.Settings.Password,
        // needs ToString() to store as enums correctly, see https://github.com/DapperLib/Dapper/issues/813.
        MatchType = room.Settings.MatchType.ToDatabaseMatchType().ToString(),
        QueueMode = room.Settings.QueueMode.ToDatabaseQueueMode().ToString()
      });
    }

    public async Task UpdateRoomStatusAsync(MultiplayerRoom room)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("UPDATE rooms SET status = @Status WHERE id = @RoomID", new
      {
        RoomID = room.RoomID,
        // needs ToString() to store as enums correctly, see https://github.com/DapperLib/Dapper/issues/813.
        Status = room.State.ToDatabaseRoomStatus().ToString(),
      });
    }

    public async Task UpdateRoomHostAsync(MultiplayerRoom room)
    {
      var connection = await getConnectionAsync();

      Debug.Assert(room.Host != null);

      try
      {
        await connection.ExecuteAsync("UPDATE rooms SET host_id = @HostUserID WHERE id = @RoomID", new
        {
          HostUserID = room.Host.UserID,
          RoomID = room.RoomID
        });
      }
      catch (MySqlException)
      {
        // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
      }
    }

    public async Task AddRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user)
    {
      var connection = await getConnectionAsync();

      try
      {
        using (var transaction = await connection.BeginTransactionAsync())
        {
          // the user may have previously been in the room and set some scores, so need to update their presence if existing.
          await connection.ExecuteAsync("INSERT INTO room_participated_users (room_id, user_id, joined_at, left_at) VALUES (@RoomID, @UserID, NOW(), NULL) ON DUPLICATE KEY UPDATE left_at = NULL", new
          {
            RoomID = room.RoomID,
            UserID = user.UserID
          }, transaction);

          await connection.ExecuteAsync("UPDATE rooms SET participant_count = @Count WHERE id = @RoomID", new
          {
            RoomID = room.RoomID,
            Count = room.Users.Count
          }, transaction);

          await transaction.CommitAsync();
        }
      }
      catch (MySqlException)
      {
        // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
      }
    }

    public Task AddLoginForUserAsync(int userId, string? userIp)
    {
      return Task.CompletedTask;
    }

    public async Task RemoveRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user)
    {
      var connection = await getConnectionAsync();

      try
      {
        using (var transaction = await connection.BeginTransactionAsync())
        {
          await connection.ExecuteAsync("UPDATE room_participated_users SET left_at = NOW() WHERE room_id = @RoomID AND user_id = @UserID AND left_at IS NULL", new
          {
            RoomID = room.RoomID,
            UserID = user.UserID
          }, transaction);

          await connection.ExecuteAsync("UPDATE rooms SET participant_count = @Count WHERE id = @RoomID", new
          {
            RoomID = room.RoomID,
            Count = room.Users.Count
          }, transaction);

          await transaction.CommitAsync();
        }
      }
      catch (MySqlException)
      {
        // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
      }
    }

    public async Task<multiplayer_playlist_item> GetPlaylistItemAsync(long roomId, long playlistItemId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleAsync<multiplayer_playlist_item>(
          "SELECT p.*, b.difficulty_rating FROM room_playlists p JOIN beatmaps b ON p.beatmap_id = b.id WHERE p.id = @Id AND p.room_id = @RoomId", new
          {
            Id = playlistItemId,
            RoomId = roomId
          });
    }

    public async Task<long> AddPlaylistItemAsync(multiplayer_playlist_item item)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync(@"
                INSERT INTO room_playlists
                    (id, owner_id, room_id, beatmap_id, ruleset_id,
                     allowed_mods, required_mods, freestyle, playlist_order,
                     expired, played_at)
                VALUES
                    (
                        (SELECT COALESCE(MAX(rp.id), -1) + 1
                         FROM room_playlists rp
                         WHERE rp.room_id = @room_id),
                        @owner_id, @room_id, @beatmap_id, @ruleset_id,
                        @allowed_mods, @required_mods, @freestyle, @playlist_order,
                        @expired, @played_at 
                    );",
        new {
          item.owner_id,
          item.room_id,
          item.beatmap_id,
          item.ruleset_id,
          item.allowed_mods,
          item.required_mods,
          item.freestyle,
          item.playlist_order,
          item.expired,
          item.played_at
        });

      return await connection.QuerySingleAsync<long>(@"
                SELECT id FROM room_playlists WHERE db_id = LAST_INSERT_ID();");
    }

    public async Task UpdatePlaylistItemAsync(multiplayer_playlist_item item)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync(
          "UPDATE room_playlists SET"
          + " beatmap_id = @beatmap_id,"
          + " ruleset_id = @ruleset_id,"
          + " required_mods = @required_mods,"
          + " allowed_mods = @allowed_mods,"
          + " freestyle = @freestyle,"
          + " playlist_order = @playlist_order,"
          + " updated_at = NOW()"
          + " WHERE id = @id AND room_id = @room_id", new {
            item.beatmap_id,
            item.ruleset_id,
            item.required_mods,
            item.allowed_mods,
            item.freestyle,
            item.playlist_order,
            item.id,
            item.room_id,
          });
    }

    public async Task RemovePlaylistItemAsync(long roomId, long playlistItemId)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("DELETE FROM room_playlists WHERE id = @Id AND room_id = @RoomId", new
      {
        Id = playlistItemId,
        RoomId = roomId
      });
    }

    public async Task MarkPlaylistItemAsPlayedAsync(long roomId, long playlistItemId)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("UPDATE room_playlists SET expired = 1, played_at = NOW(), updated_at = NOW() WHERE id = @PlaylistItemId AND room_id = @RoomId", new
      {
        PlaylistItemId = playlistItemId,
        RoomId = roomId
      });
    }

    public async Task EndMatchAsync(MultiplayerRoom room)
    {
      var connection = await getConnectionAsync();

      // Expire all non-expired items from the playlist.
      await connection.ExecuteAsync(
          "UPDATE room_playlists p"
          + " SET p.expired = 1, played_at = NOW(), updated_at = NOW()"
          + " WHERE p.room_id = @RoomID"
          + " AND p.expired = 0",
          new
          {
            RoomID = room.RoomID
          });

      int totalUsers = connection.QuerySingle<int>("SELECT COUNT(*) FROM room_participated_users WHERE room_id = @RoomID", new { RoomID = room.RoomID });

      // Close the room.
      await connection.ExecuteAsync("UPDATE rooms SET participant_count = @Count, ends_at = NOW() WHERE id = @RoomID", new
      {
        RoomID = room.RoomID,
        Count = totalUsers,
      });
    }

    public async Task<multiplayer_playlist_item[]> GetAllPlaylistItemsAsync(long roomId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<multiplayer_playlist_item>(
          "SELECT p.*, b.difficulty_rating FROM room_playlists p JOIN beatmaps b ON p.beatmap_id = b.id WHERE p.room_id = @RoomId", new
          {
            RoomId = roomId
          })).ToArray();
    }

    public async Task MarkScoreHasReplay(Score score)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("UPDATE `scores` SET `has_replay` = 1 WHERE `id` = @scoreId", new
      {
        scoreId = score.ScoreInfo.OnlineID,
      });
    }

    public async Task<int?> GetUserIdFromScoreTokenAsync(long scoreToken)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<int?>(
          "SELECT `user_id` FROM `score_tokens` WHERE `id` = @Id", new
          {
            Id = scoreToken
          });
    }

    public async Task<SoloScore?> GetScoreFromTokenAsync(long token)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<SoloScore?>(
          "SELECT * FROM `scores` WHERE `id` = (SELECT `score_id` FROM `score_tokens` WHERE `id` = @Id)", new
          {
            Id = token
          });
    }

    public async Task<SoloScore?> GetScoreAsync(long id)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<SoloScore?>("SELECT * FROM `scores` WHERE `id` = @Id", new
      {
        Id = id
      });
    }

    public async Task<bool> IsScoreProcessedAsync(long scoreId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<bool>("SELECT 1 FROM `scores` WHERE `id` = @ScoreId AND `processed` = '1'", new
      {
        ScoreId = scoreId
      });
    }

    public async Task<phpbb_zebra?> GetUserRelation(int userId, int zebraId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<phpbb_zebra?>(
          "SELECT user_id, target_id AS zebra_id, type IN ('FOLLOW', 'friend') AS friend, "
          + "type = 'BLOCK' AS foe FROM relationship "
          + "WHERE user_id = @UserId AND target_id = @ZebraId ORDER BY (type = 'BLOCK') DESC LIMIT 1", new
      {
        UserId = userId,
        ZebraId = zebraId
      });
    }

    public async Task<IEnumerable<int>> GetUserFriendsAsync(int userId)
    {
      var connection = await getConnectionAsync();

      return await connection.QueryAsync<int>(
          "SELECT r.target_id FROM relationship r "
          + "JOIN lazer_users u ON r.target_id = u.id "
          + "WHERE r.user_id = @UserId "
          + "AND r.type IN ('FOLLOW', 'friend') "
          + "AND u.is_active = 1", new { UserId = userId });
    }

    public async Task<bool> GetUserAllowsPMs(int userId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<bool>("SELECT NOT `pm_friends_only` FROM `lazer_users` WHERE `id` = @UserId", new
      {
        UserId = userId
      });
    }

    public Task<osu_build?> GetBuildByIdAsync(int buildId)
    {
      return Task.FromResult<osu_build?>(new osu_build
      {
        build_id = (uint)buildId,
        allow_bancho = true,
        hash = null,
        users = 0,
        version = null,
      });
    }

    public Task<osu_build?> GetBuildByHashAsync(string hash)
    {
      return Task.FromResult<osu_build?>(new osu_build
      {
        build_id = 0,
        allow_bancho = true,
        hash = null,
        users = 0,
        version = null,
      });
    }

    public Task<IEnumerable<osu_build>> GetAllMainLazerBuildsAsync()
    {
      return Task.FromResult<IEnumerable<osu_build>>(Array.Empty<osu_build>());
    }

    public Task<IEnumerable<osu_build>> GetAllPlatformSpecificLazerBuildsAsync()
    {
      return Task.FromResult<IEnumerable<osu_build>>(Array.Empty<osu_build>());
    }

    public Task UpdateBuildUserCountAsync(osu_build build)
    {
      return Task.CompletedTask;
    }

    public Task<IEnumerable<chat_filter>> GetAllChatFiltersAsync()
    {
      return Task.FromResult<IEnumerable<chat_filter>>(Array.Empty<chat_filter>());
    }

    public async Task<IEnumerable<multiplayer_room>> GetActiveDailyChallengeRoomsAsync()
    {
      var connection = await getConnectionAsync();

      return await connection.QueryAsync<multiplayer_room>(
          "SELECT * FROM `rooms` "
          + "WHERE `category` = 'daily_challenge' "
          + "AND `type` = 'playlists' "
          + "AND `starts_at` <= NOW() "
          + "AND `ends_at` > NOW()");
    }

    public async Task<(long roomID, long playlistItemID)?> GetMultiplayerRoomIdForScoreAsync(long scoreId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<(long, long)?>(
          "SELECT `room_id`, `playlist_item_id` FROM `scores` WHERE `id` = @scoreId",
          new { scoreId = scoreId });
    }

    public async Task<bool> AnyScoreTokenExistsFor(long playlistItemId, long roomId)
    {
      var connection = await getConnectionAsync();

      var scoreTokenCount = await connection.QuerySingleAsync<long>(
          "SELECT COUNT(1) FROM `score_tokens` WHERE `playlist_item_id` = @playlistItemId AND `room_id` = @roomId",
          new { playlistItemId = playlistItemId, roomId = roomId });

      return scoreTokenCount > 0;
    }

    /// <summary>
    /// Retrieves ALL score data for scores on a playlist item.
    /// </summary>
    /// <remarks>
    /// This should be used sparingly as it queries full rows.
    /// </remarks>
    public async Task<IEnumerable<SoloScore>> GetAllScoresForPlaylistItem(long playlistItemId)
    {
      var connection = await getConnectionAsync();

      return await connection.QueryAsync<SoloScore>(
          "SELECT s.* FROM `scores` s "
          + "JOIN `playlist_best_scores` pbs ON pbs.`score_id` = s.`id` "
          + "WHERE pbs.`playlist_id` = @playlistItemId", new
          {
            playlistItemId = playlistItemId,
          });
    }

    public async Task<IEnumerable<SoloScore>> GetAllScoresForPlaylistItem(long roomId, long playlistItemId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<SoloScore>(
          "SELECT s.* FROM `scores` s "
          + "JOIN `playlist_best_scores` pbs ON pbs.`score_id` = s.`id` "
          + "WHERE pbs.`playlist_id` = @playlistItemId AND pbs.`room_id` = @roomId", new
          {
            playlistItemId = playlistItemId,
            roomId = roomId
          }));
    }

    /// <summary>
    /// Retrieves the passing score ids and total scores on a playlist item.
    /// </summary>
    public async Task<IEnumerable<SoloScore>> GetPassingScoresForPlaylistItem(long roomId, long playlistItemId, ulong afterScoreId = 0)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<SoloScore>(
          "SELECT s.`id`, s.`total_score` FROM `scores` s "
          + "JOIN `playlist_best_scores` pbs ON pbs.`score_id` = s.`id` "
          + "WHERE s.`passed` = 1 "
          + "AND pbs.`playlist_id` = @playlistItemId "
          + "AND pbs.`room_id` = @roomId "
          + "AND pbs.`score_id` > @afterScoreId", new
          {
            playlistItemId = playlistItemId,
            roomId = roomId,
            afterScoreId = afterScoreId,
          }));
    }

    public async Task<playlist_best_score?> GetUserBestScoreAsync(long roomId, long playlistItemId, int userId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<playlist_best_score>(
          "SELECT * FROM `playlist_best_scores` WHERE `playlist_id` = @playlistItemId AND `room_id` = @roomId AND `user_id` = @userId", new
          {
            playlistItemId = playlistItemId,
            roomId = roomId,
            userId = userId
          });
    }

    public async Task<int> GetUserRankInRoomAsync(long roomId, long playlistItemId, ulong scoreId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleAsync<int>(
          "WITH `user_score` AS (SELECT `total_score` FROM `playlist_best_scores` WHERE `room_id` = @roomId AND `playlist_id` = @playlistItemId AND `score_id` = @scoreId) "
          + "SELECT COUNT(1) + 1 FROM `playlist_best_scores` "
          + "WHERE `room_id` = @roomId "
          + "AND `playlist_id` = @playlistItemId "
          + "AND `score_id` != @scoreId "
          + "AND `total_score` > (SELECT `total_score` FROM `user_score`)",
          new
          {
            roomId = roomId,
            playlistItemId = playlistItemId,
            scoreId = scoreId,
          });
    }

    public async Task LogRoomEventAsync(multiplayer_realtime_room_event ev)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync(
          "INSERT INTO `multiplayer_events` (`room_id`, `event_type`, `playlist_item_id`, `user_id`, `event_detail`, `created_at`, `updated_at`) "
          + "VALUES (@room_id, @event_type, @playlist_item_id, @user_id, @event_detail, NOW(), NOW())",
          ev);
    }

    public async Task LogRoomEventAsync(matchmaking_room_event ev)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync(
          "INSERT INTO `matchmaking_room_events` (`room_id`, `event_type`, `playlist_item_id`, `user_id`, `event_detail`, `created_at`, `updated_at`) "
          + "VALUES (@room_id, @event_type, @playlist_item_id, @user_id, @event_detail, NOW(), NOW())",
          ev);
    }

    public async Task<IEnumerable<beatmap_sync>> GetChangedBeatmapSetsAsync(DateTimeOffset after)
    {
      var connection = await getConnectionAsync();
      return await connection.QueryAsync<beatmap_sync>(
          "SELECT `beatmapset_id`, `updated_at` FROM beatmapsync WHERE updated_at > @After",
          new { After = after });
    }

    public Task ToggleUserPresenceAsync(int userId, bool visible)
    {
      return Task.CompletedTask;
    }

    public async Task<float> GetUserPPAsync(int userId, int rulesetId, int variant)
    {
      var connection = await getConnectionAsync();
      var ruleset = manager.GetRuleset(rulesetId);
      return await connection.QuerySingleOrDefaultAsync<float>(
          "SELECT `pp` FROM lazer_user_statistics WHERE `user_id` = @userId AND `mode` = @mode",
          new { userId = userId, mode = ruleset.ShortName });
    }

    public async Task<matchmaking_pool[]> GetActiveMatchmakingPoolsAsync()
    {
      return await sharedInterop.GetMatchmakingPoolsAsync();
    }

    public async Task<matchmaking_pool?> GetMatchmakingPoolAsync(uint poolId)
    {
      var connection = await getConnectionAsync();

      var pool = await connection.QuerySingleOrDefaultAsync<matchmaking_pool>("SELECT * FROM `matchmaking_pools` WHERE `id` = @PoolId", new
      {
        PoolId = poolId
      });
      // The standard Ranked lobby remains viewable. Queue/duel/room entry
      // points enforce the SOMS disable policy; do not make lobby entry fail.
      return pool;
    }

    public async Task<matchmaking_pool_beatmap[]> GetMatchmakingPoolBeatmapsAsync(uint poolId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<matchmaking_pool_beatmap>("SELECT p.*, CASE b.mode WHEN 'TAIKO' THEN 1 WHEN 'FRUITS' THEN 2 WHEN 'MANIA' THEN 3 ELSE 0 END AS playmode, b.checksum, b.difficulty_rating FROM `matchmaking_pool_beatmaps` p "
                                                                    + "JOIN `beatmaps` b ON p.beatmap_id = b.id "
                                                                    + "WHERE p.pool_id = @PoolId", new
                                                                    {
                                                                      PoolId = poolId
                                                                    })).ToArray();
    }

    public async Task<matchmaking_pool_beatmap?> GetMatchmakingPoolBeatmapAsync(uint poolId, int beatmapId, string mods)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<matchmaking_pool_beatmap>("SELECT p.*, CASE b.mode WHEN 'TAIKO' THEN 1 WHEN 'FRUITS' THEN 2 WHEN 'MANIA' THEN 3 ELSE 0 END AS playmode, b.checksum, b.difficulty_rating FROM `matchmaking_pool_beatmaps` p "
                                                                                  + "JOIN `beatmaps` b ON p.beatmap_id = b.id "
                                                                                  + "WHERE p.pool_id = @PoolId "
                                                                                  + "AND p.beatmap_id = @BeatmapId "
                                                                                  + "AND COALESCE(p.mods, JSON_ARRAY()) = CAST(@Mods AS JSON)", new
                                                                                  {
                                                                                    PoolId = poolId,
                                                                                    BeatmapId = beatmapId,
                                                                                    Mods = string.IsNullOrEmpty(mods) ? "[]" : mods
                                                                                  });
    }

    public async Task UpdateMatchmakingPoolBeatmapRatingAsync(matchmaking_pool_beatmap beatmap)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("INSERT INTO `matchmaking_pool_beatmaps` (pool_id, beatmap_id, mods, rating, rating_sig, selection_count) "
                                    + "VALUES (@PoolId, @BeatmapId, @Mods, @Rating, @RatingSig, 0) "
                                    + "ON DUPLICATE KEY UPDATE rating = @Rating, rating_sig = @RatingSig", new
                                    {
                                      PoolId = beatmap.pool_id,
                                      BeatmapId = beatmap.beatmap_id,
                                      Mods = string.IsNullOrEmpty(beatmap.mods) ? "[]" : beatmap.mods,
                                      Rating = beatmap.rating,
                                      RatingSig = beatmap.rating_sig
                                    });
    }

    public async Task<database_beatmap[]> GetMatchmakingGlobalPoolBeatmapsAsync(int rulesetId, int variant, uint poolId)
    {
      // The app owns local rank overrides, checksum pins, and review events.
      // Use its effective policy rather than an upstream-only SQL filter.
      return await sharedInterop.GetMatchmakingBeatmapsAsync(rulesetId, variant, poolId);
    }

    public async Task<matchmaking_user_stats?> GetMatchmakingUserStatsAsync(int userId, uint poolId)
    {
      var connection = await getConnectionAsync();

      return await connection.QuerySingleOrDefaultAsync<matchmaking_user_stats>("SELECT * FROM `matchmaking_user_stats` WHERE `user_id` = @UserId AND `pool_id` = @PoolId", new
      {
        UserId = userId,
        PoolId = poolId
      });
    }

    public async Task UpdateMatchmakingUserStatsAsync(matchmaking_user_stats stats)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("INSERT INTO `matchmaking_user_stats` (`user_id`, `pool_id`, `first_placements`, `total_points`, `elo_data`, `created_at`, `updated_at`) "
                                    + "VALUES (@UserId, @PoolId, @FirstPlacements, @TotalPoints, @EloData, NOW(), NOW()) "
                                    + "ON DUPLICATE KEY UPDATE "
                                    + "`first_placements` = @FirstPlacements, "
                                    + "`total_points` = @TotalPoints, "
                                    + "`elo_data` = @EloData, "
                                    + "`updated_at` = NOW()", new
                                    {
                                      UserId = stats.user_id,
                                      PoolId = stats.pool_id,
                                      FirstPlacements = stats.first_placements,
                                      TotalPoints = stats.total_points,
                                      EloData = stats.elo_data
                                    });
    }

    public async Task InsertUserEloHistoryEntry(ulong roomId, uint poolId, uint userId, uint opponentId, matchmaking_room_result result, int eloBefore, int eloAfter)
    {
      var connection = await getConnectionAsync();

      await connection.ExecuteAsync("INSERT INTO `matchmaking_user_elo_history` (room_id, pool_id, user_id, opponent_id, result, elo_before, elo_after, created_at, updated_at) "
                                    + "VALUES (@RoomId, @PoolId, @UserId, @OpponentId, @Result, @EloBefore, @EloAfter, NOW(), NOW())", new
                                    {
                                      RoomId = roomId,
                                      PoolId = poolId,
                                      UserId = userId,
                                      OpponentId = opponentId,
                                      Result = result.ToString(),
                                      EloBefore = eloBefore,
                                      EloAfter = eloAfter
                                    });
    }

    public async Task UpdateUserOnlineStatusAsync(int userId, bool isOnline)
    {
      var connection = await getConnectionAsync();

      string query = isOnline
          ? "UPDATE lazer_users SET is_online = @IsOnline WHERE id = @UserId"
          : "UPDATE lazer_users SET is_online = @IsOnline, last_visit = NOW() WHERE id = @UserId";

      await connection.ExecuteAsync(query, new { IsOnline = isOnline, UserId = userId });
    }

    public async Task<int[]> GetMatchmakingPoolRatingsAsync(uint poolId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<matchmaking_user_stats>("SELECT * FROM matchmaking_user_stats WHERE pool_id = @PoolId", new
      {
        PoolId = poolId
      })).Select(stats => (int)Math.Round(stats.EloData.Rating.Mu)).ToArray();
    }

    public async Task<int[]> GetMatchmakingPoolTop100RatingsAsync(uint poolId)
    {
      var connection = await getConnectionAsync();

      return (await connection.QueryAsync<matchmaking_user_stats>("SELECT * FROM matchmaking_user_stats WHERE pool_id = @PoolId", new
      {
        PoolId = poolId
      })).Select(stats => (int)Math.Round(stats.EloData.Rating.Mu))
        .OrderByDescending(rating => rating)
        .Take(100)
        .ToArray();
    }

    public void Dispose()
    {
      openConnection?.Dispose();
    }

    private async Task<MySqlConnection> getConnectionAsync()
    {
      if (openConnection != null)
        return openConnection;

      DapperExtensions.InstallDateTimeOffsetMapper();

      openConnection = new MySqlConnection(
          $"Server={AppSettings.DatabaseHost};Port={AppSettings.DatabasePort};Database={AppSettings.DatabaseName};User={AppSettings.DatabaseUser};Password={AppSettings.DatabasePassword};ConnectionTimeout=5;ConnectionReset=false;Pooling=true;MaximumPoolSize=10");

      await openConnection.OpenAsync();

      return openConnection;
    }
  }
}
