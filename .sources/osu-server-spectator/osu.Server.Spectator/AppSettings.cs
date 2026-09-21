// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;

namespace osu.Server.Spectator
{
  public static class AppSettings
  {
    public static bool SaveReplays { get; set; }
    public static int ReplayUploaderConcurrency { get; set; } = 1;

    #region Sync With g0v0-server

    public static bool EnableAllBeatmapLeaderboard { get; set; }

    // ReSharper disable once InconsistentNaming
    public static bool EnableAP { get; set; }

    // ReSharper disable once InconsistentNaming
    public static bool EnableRX { get; set; }

    #endregion

    public static bool TrackBuildUserCounts { get; set; }
    public static bool ClientCheckVersion { get; set; }
    public static int[] ClientCheckVersionExemptGroups { get; set; }

    public static int ServerPort { get; set; } = 8086;
    public static string RedisHost { get; } = "localhost";
    public static string DataDogAgentHost { get; set; } = "localhost";

    public static string DatabaseHost { get; } = "localhost";
    public static string DatabaseUser { get; } = "osu_api";
    public static string DatabasePassword { get; } = "passsword";
    public static string DatabaseName { get; } = "osu_api";
    public static int DatabasePort { get; } = 3306;

    public static string SharedInteropDomain { get; } = "http://localhost:8000";
    public static string SharedInteropSecret { get; } = string.Empty;

    public static string? SentryDsn { get; }

    #region JWT Authentication Settings

    public static string JwtSecretKey { get; } = "your_jwt_secret_here";
    public static string JwtAlgorithm { get; } = "HS256";
    public static int JwtAccessTokenExpireMinutes { get; } = 1440;
    public static int OsuClientId { get; } = 5;
    public static bool UseLegacyRsaAuth { get; }

    #endregion

    #region Custom rulesets

    public static string RulesetsPath { get; }

    public static bool CheckRulesetVersion { get; set; }

    #endregion

    // app.const
    // BANCHOBOT_ID = 2
    public static int BanchoBotUserId { get; } = 2;

    public static int MatchmakingRoomRounds { get; set; } = 5;
    public static bool MatchmakingHeadToHeadIsBestOf { get; set; } = true;
    public static bool MatchmakingRoomAllowSkip { get; set; }
    public static TimeSpan MatchmakingLobbyUpdateRate { get; } = TimeSpan.FromSeconds(5);
    public static TimeSpan MatchmakingQueueUpdateRate { get; } = TimeSpan.FromSeconds(1);
    public static TimeSpan MatchmakingRecentMatchupTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether to issue bans from the matchmaking queue for various abuse.
    /// </summary>
    public static bool MatchmakingQueueAllowBans { get; } = true;

    /// <summary>
    /// Replaces matchmaking pool beatmaps with a debug set.
    /// </summary>
    public static bool MatchmakingDebugBeatmaps { get; }

    /// <summary>
    /// The duration for which users are temporarily banned from the matchmaking queue after declining an invitation.
    /// </summary>
    public static TimeSpan MatchmakingQueueBanDuration { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The total number of beatmaps per matchmaking room.
    /// </summary>
    public static int MatchmakingPoolSize { get; set; } = 50;

    static AppSettings()
    {
      SaveReplays = bool.TryParse(Environment.GetEnvironmentVariable("SAVE_REPLAYS"), out bool saveReplays) ? saveReplays : SaveReplays;
      ReplayUploaderConcurrency = int.TryParse(Environment.GetEnvironmentVariable("REPLAY_UPLOAD_THREADS"), out int uploaderConcurrency) ? uploaderConcurrency : ReplayUploaderConcurrency;
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReplayUploaderConcurrency);

      EnableAllBeatmapLeaderboard = bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_ALL_BEATMAP_LEADERBOARD"), out bool enableAllBeatmapLeaderboard)
          ? enableAllBeatmapLeaderboard
          : EnableAllBeatmapLeaderboard;
      EnableAP = bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_AP") ?? Environment.GetEnvironmentVariable("ENABLE_OSU_AP"), out bool enableAP) ? enableAP : EnableAP;
      EnableRX = bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_RX") ?? Environment.GetEnvironmentVariable("ENABLE_OSU_RX"), out bool enableRX) ? enableRX : EnableRX;

      TrackBuildUserCounts = bool.TryParse(Environment.GetEnvironmentVariable("TRACK_BUILD_USER_COUNTS"), out bool trackBuildUserCounts) ? trackBuildUserCounts : TrackBuildUserCounts;
      ClientCheckVersion = bool.TryParse(Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION"), out bool clientCheckVersion) ? clientCheckVersion : ClientCheckVersion;

      ClientCheckVersionExemptGroups = Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION_EXEMPT_GROUPS")?.Split(',')
                                                  .Select(id =>
                                                  {
                                                    bool parsed = int.TryParse(id, out int result);
                                                    return (parsed, result);
                                                  })
                                                  .Where(res => res.parsed)
                                                  .Select(res => res.result)
                                                  .ToArray() ?? [];

      ServerPort = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out int serverPort) ? serverPort : ServerPort;
      RedisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? RedisHost;
      DataDogAgentHost = Environment.GetEnvironmentVariable("DD_AGENT_HOST") ?? DataDogAgentHost;

      DatabaseHost = Environment.GetEnvironmentVariable("MYSQL_HOST") ?? DatabaseHost;
      DatabaseUser = Environment.GetEnvironmentVariable("MYSQL_USER") ?? DatabaseUser;
      DatabasePort = int.TryParse(Environment.GetEnvironmentVariable("MYSQL_PORT"), out int databasePort) ? databasePort : DatabasePort;
      DatabasePassword = Environment.GetEnvironmentVariable("MYSQL_PASSWORD") ?? DatabasePassword;
      DatabaseName = Environment.GetEnvironmentVariable("MYSQL_DATABASE") ?? DatabaseName;

      SharedInteropDomain = Environment.GetEnvironmentVariable("SHARED_INTEROP_DOMAIN") ?? SharedInteropDomain;
      SharedInteropSecret = Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET") ?? SharedInteropSecret;

      SentryDsn = Environment.GetEnvironmentVariable("SP_SENTRY_DSN") ?? null;

      // JWT Authentication Settings
      JwtSecretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? JwtSecretKey;
      JwtAlgorithm = Environment.GetEnvironmentVariable("JWT_ALGORITHM") ?? JwtAlgorithm;
      JwtAccessTokenExpireMinutes = int.TryParse(Environment.GetEnvironmentVariable("JWT_ACCESS_TOKEN_EXPIRE_MINUTES"), out int jwtExpireMinutes)
          ? jwtExpireMinutes
          : JwtAccessTokenExpireMinutes;
      OsuClientId = int.TryParse(Environment.GetEnvironmentVariable("OSU_CLIENT_ID"), out int osuClientId) ? osuClientId : OsuClientId;
      UseLegacyRsaAuth = bool.TryParse(Environment.GetEnvironmentVariable("USE_LEGACY_RSA_AUTH"), out bool useLegacyRsaAuth) ? useLegacyRsaAuth : UseLegacyRsaAuth;

      BanchoBotUserId = int.TryParse(Environment.GetEnvironmentVariable("BANCHO_BOT_USER_ID"), out int banchoBotUserId) ? banchoBotUserId : BanchoBotUserId;

      MatchmakingRoomRounds = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_ROOM_ROUNDS"), out int mmRounds)
          ? mmRounds
          : MatchmakingRoomRounds;

      MatchmakingHeadToHeadIsBestOf = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_HEAD_TO_HEAD_IS_BESTOF"), out bool mmHeadToHeadIsBestOf)
          ? mmHeadToHeadIsBestOf
          : MatchmakingHeadToHeadIsBestOf;

      MatchmakingRoomAllowSkip = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_ALLOW_SKIP"), out bool mmAllowSkip)
          ? mmAllowSkip
          : MatchmakingRoomAllowSkip;

      MatchmakingLobbyUpdateRate = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_LOBBY_UPDATE_RATE"), out int mmLobbyUpdateRate)
          ? TimeSpan.FromSeconds(mmLobbyUpdateRate)
          : MatchmakingLobbyUpdateRate;

      MatchmakingQueueUpdateRate = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_QUEUE_UPDATE_RATE"), out int mmQueueUpdateRate)
          ? TimeSpan.FromSeconds(mmQueueUpdateRate)
          : MatchmakingQueueUpdateRate;

      MatchmakingRecentMatchupTimeout = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_RECENT_MATCHUP_TIMEOUT"), out int mmRecentMatchupTimeout)
          ? TimeSpan.FromSeconds(mmRecentMatchupTimeout)
          : MatchmakingRecentMatchupTimeout;

      MatchmakingQueueAllowBans = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_QUEUE_ALLOW_BANS"), out bool mmQueueAllowBans)
          ? mmQueueAllowBans
          : MatchmakingQueueAllowBans;

      MatchmakingDebugBeatmaps = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_DEBUG_BEATMAPS"), out bool mmDebugBeatmaps)
          ? mmDebugBeatmaps
          : MatchmakingDebugBeatmaps;

      MatchmakingQueueBanDuration = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_QUEUE_BAN_DURATION"), out int mmQueueBanDuration)
          ? TimeSpan.FromSeconds(mmQueueBanDuration)
          : MatchmakingQueueBanDuration;

      MatchmakingPoolSize = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_POOL_SIZE"), out int mmPoolSize)
          ? mmPoolSize
          : MatchmakingPoolSize;

      RulesetsPath = Environment.GetEnvironmentVariable("RULESETS_PATH") ?? "rulesets";

      string? checkRulesetEnv = Environment.GetEnvironmentVariable("CHECK_RULESET_VERSION");
      CheckRulesetVersion = checkRulesetEnv == null || !bool.TryParse(checkRulesetEnv, out bool isCheckRulesetVersion) || isCheckRulesetVersion;
    }
  }
}
