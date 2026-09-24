// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
  [Authorize]
  public partial class MultiplayerHub : StatefulUserHub<IMultiplayerClient, MultiplayerClientState>, IMultiplayerServer
  {
    public const string STATSD_PREFIX = "multiplayer";
    private const string ruleset_hash_header = "X-Osu-Ruleset-Hashes";

    protected readonly IMultiplayerRoomController RoomController;
    private readonly IDatabaseFactory databaseFactory;
    private readonly ChatFilters chatFilters;
    private readonly ISharedInterop sharedInterop;
    private readonly MultiplayerEventDispatcher multiplayerEventDispatcher;
    private readonly RulesetManager rulesetManager;
    private readonly IMatchmakingQueueBackgroundService matchmakingQueueService;

    public MultiplayerHub(
        ILoggerFactory loggerFactory,
        EntityStore<MultiplayerClientState> users,
        IDatabaseFactory databaseFactory,
        ChatFilters chatFilters,
        IMultiplayerRoomController roomController,
        ISharedInterop sharedInterop,
        MultiplayerEventDispatcher multiplayerEventDispatcher,
        RulesetManager rulesetManager,
        IMatchmakingQueueBackgroundService matchmakingQueueService)
        : base(loggerFactory, users)
    {
      this.databaseFactory = databaseFactory;
      this.chatFilters = chatFilters;
      this.sharedInterop = sharedInterop;
      this.multiplayerEventDispatcher = multiplayerEventDispatcher;
      this.rulesetManager = rulesetManager;
      this.matchmakingQueueService = matchmakingQueueService;

      RoomController = roomController;
    }

    public override async Task OnConnectedAsync()
    {
      await base.OnConnectedAsync();

      Dictionary<string, string> rulesetHashes = new Dictionary<string, string>();

      using (var usage = await GetOrCreateLocalUserState())
      {
        try
        {
          if (Context.GetHttpContext()?.Request.Headers.TryGetValue(ruleset_hash_header, out StringValues headerValue) == true)
          {
            Dictionary<string, string>? parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(headerValue.ToString());

            if (parsed != null)
            {
              rulesetHashes = parsed;
            }
          }
        }
        catch
        {
          // GetHttpContext may throw if the context does not support Features (e.g. in tests).
        }

        Log("Connected with ruleset hashes: " + string.Join(", ", rulesetHashes.Select(kvp => $"{kvp.Key}: {kvp.Value}")));

        usage.Item = new MultiplayerClientState(Context.ConnectionId, Context.GetUserId(), rulesetHashes: rulesetHashes);
      }
    }

    public async Task<MultiplayerRoom> CreateRoom(MultiplayerRoom room)
    {
      ArgumentNullException.ThrowIfNull(room);
      if (room.Settings.MatchType == MatchType.RankedPlay)
        throw new InvalidStateException("Иди в обычный лазер");

      Log("Attempting to create room");

      using (var db = databaseFactory.GetInstance())
      {
        if (await db.IsUserRestrictedAsync(Context.GetUserId()))
          throw new InvalidStateException("Can't join a room when restricted.");
      }

      long roomId = await sharedInterop.CreateRoomAsync(Context.GetUserId(), room);
      await multiplayerEventDispatcher.PostRoomCreatedAsync(roomId, Context.GetUserId());

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        if (userUsage.Item.CurrentRoomID != null)
          throw new InvalidStateException("Can't join a room when already in another room.");

        return await RoomController.CreateRoom(userUsage.Item, roomId, room.Settings.Password);
      }
    }

    public Task<MultiplayerRoom> JoinRoom(long roomId) => JoinRoomWithPassword(roomId, string.Empty);

    public async Task<MultiplayerRoom> JoinRoomWithPassword(long roomId, string password)
    {
      Log($"Attempting to join room {roomId}");
      using (var db = databaseFactory.GetInstance())
      {
        if ((await db.GetRoomAsync(roomId))?.type == osu.Server.Spectator.Database.Models.database_match_type.ranked_play)
          throw new InvalidStateException("Иди в обычный лазер");
      }

      using (var db = databaseFactory.GetInstance())
      {
        if (await db.IsUserRestrictedAsync(Context.GetUserId()))
          throw new InvalidStateException("Can't join a room when restricted.");
      }

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        if (userUsage.Item.CurrentRoomID != null)
          throw new InvalidStateException("Can't join a room when already in another room.");

        var room = await RoomController.JoinRoom(userUsage.Item, roomId, password);

        var result = await room.Playlist.ValidateRulesets(rulesetManager, userUsage.Item.RulesetHashes);

        if (result.Count > 0)
        {
          // Leave the room before throwing, as the user has already been joined.
          using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            await RoomController.LeaveRoom(userUsage.Item, roomUsage);

          throw new InvalidStateException("You have one or more outdated or invalid rulesets to join room: "
                                          + string.Join(", ", result.Select(r => $"(name: {r.Item1}, server-ver: {r.Item2})")));
        }

        return room;
      }
    }

    public async Task LeaveRoom()
    {
      Log("Requesting to leave room");

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        if (userUsage.Item.CurrentRoomID == null)
          return;

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
          await RoomController.LeaveRoom(userUsage.Item, roomUsage);
      }
    }

    public async Task InvitePlayer(int userId)
    {
      using (var db = databaseFactory.GetInstance())
      {
        bool isRestricted = await db.IsUserRestrictedAsync(userId);
        if (isRestricted)
          throw new InvalidStateException("Can't invite a restricted user to a room.");

        var relation = await db.GetUserRelation(Context.GetUserId(), userId);

        // The local user has the player they are trying to invite blocked.
        if (relation?.foe == true)
          throw new UserBlockedException();

        var inverseRelation = await db.GetUserRelation(userId, Context.GetUserId());

        // The player being invited has the local user blocked.
        if (inverseRelation?.foe == true)
          throw new UserBlockedException();

        // The player being invited disallows unsolicited PMs and the local user is not their friend.
        if (inverseRelation?.friend != true && !await db.GetUserAllowsPMs(userId))
          throw new UserBlocksPMsException();
      }

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var user = userUsage.Item;
          var room = roomUsage.Item;

          if (user == null)
            throw new InvalidStateException("Local user was not found in the expected room");

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.InvitePlayer(invitedUserId: userId, invitedBy: user.UserId);
        }
      }
    }

    public async Task TransferHost(int userId)
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          room.Log($"Transferring host from {room.Host?.UserID} to {userId}");

          ensureIsHostOrReferee(room);

          await room.SetHost(userId);
        }
      }
    }

    public async Task KickUser(int userId)
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          room.Log($"Kicking user {userId}");

          if (userId == userUsage.Item.UserId)
            throw new InvalidStateException("Can't kick self");

          ensureIsHostOrReferee(room);

          var kickTarget = room.Users.FirstOrDefault(u => u.UserID == userId);

          if (kickTarget == null)
            throw new InvalidOperationException("Target user is not in the current room");

          if (kickTarget.Role == MultiplayerRoomUserRole.Referee)
            throw new InvalidStateException("Can't kick a referee.");

          using (var targetUserUsage = await GetStateFromUser(kickTarget.UserID))
          {
            Debug.Assert(targetUserUsage.Item != null);

            if (targetUserUsage.Item.CurrentRoomID == null)
              throw new InvalidOperationException();

            await RoomController.KickUserFromRoom(targetUserUsage.Item, roomUsage, userUsage.Item.UserId);
          }
        }
      }
    }

    public async Task ChangeState(MultiplayerUserState newState)
    {
      newState.ThrowIfInvalid();

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.ChangeUserState(Context.GetUserId(), newState);
        }
      }
    }

    public async Task ChangeBeatmapAvailability(BeatmapAvailability newBeatmapAvailability)
    {
      ArgumentNullException.ThrowIfNull(newBeatmapAvailability);

      newBeatmapAvailability.State.ThrowIfInvalid();

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.ChangeUserBeatmapAvailability(Context.GetUserId(), newBeatmapAvailability);
        }
      }
    }

    public async Task ChangeUserStyle(int? beatmapId, int? rulesetId)
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.ChangeUserStyle(Context.GetUserId(), beatmapId, rulesetId);
        }
      }
    }

    public async Task ChangeUserMods(IEnumerable<APIMod> newMods)
    {
      ArgumentNullException.ThrowIfNull(newMods);

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.ChangeUserMods(Context.GetUserId(), newMods);
        }
      }
    }

    public async Task SendMatchRequest(MatchUserRequest request)
    {
      ArgumentNullException.ThrowIfNull(request);

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

          if (user == null)
            throw new InvalidOperationException("Local user was not found in the expected room");

          switch (request)
          {
            case StartMatchCountdownRequest startMatchCountdownRequest:
              ensureIsHostOrReferee(room);
              await room.StartMatchCountdown(startMatchCountdownRequest.Duration);
              break;

            case StopCountdownRequest stopCountdownRequest:
              ensureIsHostOrReferee(room);
              await room.StopCountdown(stopCountdownRequest.ID);
              break;

            default:
              await room.HandleUserRequest(user, request);
              break;
          }
        }
      }
    }

    public async Task StartMatch()
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          ensureIsHostOrReferee(room);

          if (room.Host != null && room.Host.State != MultiplayerUserState.Spectating && room.Host.State != MultiplayerUserState.Ready && room.Host.Role != MultiplayerRoomUserRole.Referee)
            throw new InvalidStateException("Can't start match when the host is not ready.");

          if (room.Users.All(u => u.State != MultiplayerUserState.Ready))
            throw new InvalidStateException("Can't start match when no users are ready.");

          await ServerMultiplayerRoom.StartMatch(room);
        }
      }
    }

    public async Task AbortMatch()
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          ensureIsHostOrReferee(room);

          await room.AbortMatch();
        }
      }
    }

    public async Task AbortGameplay()
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.AbortGameplay(Context.GetUserId());
        }
      }
    }

    public async Task VoteToSkipIntro()
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.VoteToSkipIntro(Context.GetUserId());
        }
      }
    }

    public async Task AddPlaylistItem(MultiplayerPlaylistItem item)
    {
      ArgumentNullException.ThrowIfNull(item);

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await validatePlaylistItemRulesetsForAllUsers(room, item);
          await room.AddPlaylistItem(Context.GetUserId(), item);
        }
      }
    }

    public async Task EditPlaylistItem(MultiplayerPlaylistItem item)
    {
      ArgumentNullException.ThrowIfNull(item);

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await validatePlaylistItemRulesetsForAllUsers(room, item);
          await room.EditPlaylistItem(Context.GetUserId(), item);
        }
      }
    }

    public async Task RemovePlaylistItem(long playlistItemId)
    {
      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;
          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          await room.RemovePlaylistItem(Context.GetUserId(), playlistItemId);
        }
      }
    }

    public async Task ChangeSettings(MultiplayerRoomSettings settings)
    {
      ArgumentNullException.ThrowIfNull(settings);
      if (settings.MatchType == MatchType.RankedPlay)
        throw new InvalidStateException("Иди в обычный лазер");

      settings.MatchType.ThrowIfInvalid();
      settings.QueueMode.ThrowIfInvalid();

      using (var userUsage = await GetOrCreateLocalUserState())
      {
        Debug.Assert(userUsage.Item != null);

        using (var roomUsage = await getLocalUserRoom(userUsage.Item))
        {
          var room = roomUsage.Item;

          if (room == null)
            throw new InvalidOperationException("Attempted to operate on a null room");

          ensureIsHostOrReferee(room);

          settings.Name = await chatFilters.FilterAsync(settings.Name);
          await room.ChangeRoomSettings(settings);
        }
      }
    }

    protected override async Task CleanUpState(ItemUsage<MultiplayerClientState> state)
    {
      Debug.Assert(state.Item != null);

      await base.CleanUpState(state);
      await matchmakingQueueService.RemoveFromQueueAsync(state.Item);

      if (state.Item.CurrentRoomID != null)
      {
        using (var roomUsage = await getLocalUserRoom(state.Item))
          await RoomController.LeaveRoom(state.Item, roomUsage);
      }
    }

    /// <summary>
    /// Ensure the local user is the host of the room, and throw if they are not.
    /// </summary>
    private void ensureIsHostOrReferee(MultiplayerRoom room)
    {
      if (room is ServerMultiplayerRoom { MatchController: SomsaiMatchController })
        throw new InvalidStateException("SOMSAI rooms are managed by the server.");
      bool isHost = room.Host?.UserID == Context.GetUserId();
      bool isReferee = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId())?.Role == MultiplayerRoomUserRole.Referee;

      if (!isHost && !isReferee)
        throw new NotHostException();
    }

    /// <summary>
    /// Retrieve the <see cref="MultiplayerRoom"/> for the local context user.
    /// </summary>
    private async Task<ItemUsage<ServerMultiplayerRoom>> getLocalUserRoom(MultiplayerClientState state)
    {
      if (state.CurrentRoomID == null)
        throw new NotJoinedRoomException();

      return await GetRoom(state.CurrentRoomID.Value);
    }

    private async Task checkUserToUserPermissionsAsync(int targetUser)
    {
      using (var db = databaseFactory.GetInstance())
      {
        if (await db.IsUserRestrictedAsync(targetUser))
          throw new InvalidStateException("Can't perform that action on a restricted user.");

        var relation = await db.GetUserRelation(Context.GetUserId(), targetUser);

        // The local user has the player they are trying to invite blocked.
        if (relation?.foe == true)
          throw new UserBlockedException();

        var inverseRelation = await db.GetUserRelation(targetUser, Context.GetUserId());

        // The player being invited has the local user blocked.
        if (inverseRelation?.foe == true)
          throw new UserBlockedException();

        // The player being invited disallows unsolicited PMs and the local user is not their friend.
        if (inverseRelation?.friend != true && !await db.GetUserAllowsPMs(targetUser))
          throw new UserBlocksPMsException();
      }
    }

    /// <summary>
    /// Validates that a playlist item's ruleset is compatible with all users currently in the room.
    /// </summary>
    private async Task validatePlaylistItemRulesetsForAllUsers(ServerMultiplayerRoom room, MultiplayerPlaylistItem item)
    {
      using (var db = databaseFactory.GetInstance())
      {
        foreach (var roomUser in room.Users.Where(u => u.UserID != Context.GetUserId()))
        {
          using (var usage = await GetStateFromUser(roomUser.UserID))
          {
            var result = await item.ValidateRuleset(rulesetManager, usage.Item!.RulesetHashes);

            if (result == null)
              continue;

            string? username = await db.GetUsernameAsync(roomUser.UserID);
            throw new InvalidStateException($"User {username ?? "unknown"} has non-existent or outdated rulesets: (name: {result.Item1}, server-ver: {result.Item2})");
          }
        }
      }
    }

    internal Task<ItemUsage<ServerMultiplayerRoom>> GetRoom(long roomId) => RoomController.GetRoom(roomId);
  }
}
