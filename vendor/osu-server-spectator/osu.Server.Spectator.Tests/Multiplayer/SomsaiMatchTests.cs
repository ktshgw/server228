using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Hubs.Referee;
using osu.Server.Spectator.Hubs.Referee.Models.Requests;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer
{
    public class SomsaiMatchTests : MultiplayerTest
    {
        private readonly FakeInterop interop = new FakeInterop();
        private SomsaiMatchController controller = null!;
        private ServerMultiplayerRoom room = null!;
        private readonly List<Mock<HubCallerContext>> participants = [];

        private async Task setup(int teamSize = 1)
        {
            Database.Setup(db => db.GetRealtimeRoomAsync(ROOM_ID)).Callback<long>(InitialiseRoom)
                    .ReturnsAsync(new multiplayer_room { type = database_match_type.head_to_head, tournament_mode = true, host_id = USER_ID });
            await Hub.CreateRoom(new MultiplayerRoom(ROOM_ID));
            participants.Add(ContextUser);
            participants.Add(ContextUser2);
            for (int index = 2; index < 2 * teamSize; index++)
            {
                CreateUser(3000 + index, out var context, out _);
                participants.Add(context);
            }
            foreach (var context in participants.Skip(1))
            {
                SetUserContext(context);
                await Hub.JoinRoom(ROOM_ID);
            }
            room = Rooms.GetEntityUnsafe(ROOM_ID)!;
            interop.Roster = participants.Select(context => int.Parse(context.Object.UserIdentifier!)).Chunk(teamSize).ToArray();
            interop.ItemId = room.CurrentPlaylistItem.ID;
            room.Settings.MatchType = MatchType.TeamVersus;
            controller = new SomsaiMatchController(room, DatabaseFactory.Object, EventDispatcher, RoomController, LoggerFactory.Object, interop, runPump: false);
            await room.ChangeMatchType(controller);
            SetUserContext(ContextUser);
        }

        private async Task readyAll()
        {
            foreach (var context in participants)
            {
                SetUserContext(context);
                await MarkCurrentUserReadyAndAvailable();
            }
        }

        private async Task start()
        {
            await readyAll();
            await controller.PollOnce();
            await controller.PollOnce();
            Assert.Equal(MultiplayerRoomState.WaitingForLoad, room.State);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task NativeGameplayUsesAllRosterMembersAndLockedTeams(int size)
        {
            await setup(size);
            Assert.Null(room.Host);
            Assert.True(((TeamVersusRoomState)room.MatchState!).Locked);
            Assert.Equal(2 * size, ((TeamVersusRoomState)room.MatchState!).Slots!.Length);
            for (int team = 0; team < 2; team++)
                Assert.All(room.Users.Where(user => interop.Roster[team].Contains(user.UserID)),
                    user => Assert.Equal(team, ((TeamVersusUserState)user.MatchState!).TeamID));
            await start();
            await LoadAndFinishGameplay(participants.ToArray());
            Assert.True(controller.CurrentItem.Expired);
            await controller.PollOnce();
            Assert.Equal("results", interop.Stage);
            Assert.Single(interop.Events, kind => kind == "completed");
        }

        [Fact]
        public async Task CommittedStartWithLostResponseRetriesWithoutDuplicateGameplay()
        {
            await setup();
            await readyAll();
            interop.FailStartOnce = true;
            await controller.PollOnce();
            await Assert.ThrowsAsync<HttpRequestException>(() => controller.PollOnce());
            Assert.Equal("playing", interop.Stage);
            Assert.Equal(MultiplayerRoomState.Open, room.State);
            await controller.PollOnce();
            Assert.Equal(MultiplayerRoomState.WaitingForLoad, room.State);
            await controller.PollOnce();
            Assert.Equal(2, interop.Events.Count(kind => kind == "started"));
            Receiver.Verify(client => client.RoomStateChanged(MultiplayerRoomState.WaitingForLoad), Times.Once);
        }

        [Fact]
        public async Task FailedCompletionRetriesTheSameItem()
        {
            await setup();
            await start();
            await LoadAndFinishGameplay(participants.ToArray());
            interop.FailCompletionOnce = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => controller.PollOnce());
            await controller.PollOnce();
            Assert.Equal("results", interop.Stage);
            Assert.Equal(2, interop.Events.Count(kind => kind == "completed"));
            Assert.All(interop.Items, item => Assert.Equal(interop.ItemId, item));
        }

        [Fact]
        public async Task StartAcknowledgementDoesNotLockRoomAndChangedRosterCancels()
        {
            await setup();
            await readyAll();
            await controller.PollOnce();
            interop.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task poll = controller.PollOnce();
            await interop.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            SetUserContext(ContextUser);
            await Hub.ChangeState(MultiplayerUserState.Idle).WaitAsync(TimeSpan.FromSeconds(2));
            interop.StartGate.SetResult();
            await poll;
            await controller.PollOnce();
            Assert.Equal("cancelled", interop.Stage);
            Assert.Equal(MultiplayerRoomState.Open, room.State);
            Assert.DoesNotContain(room.Users, user => user.State == MultiplayerUserState.WaitingForLoad);
        }

        [Fact]
        public async Task EmptyTournamentRoomRemainsAvailableForReconnect()
        {
            await setup();
            foreach (var context in participants)
            {
                SetUserContext(context);
                await Hub.LeaveRoom();
            }
            Assert.Same(room, Rooms.GetEntityUnsafe(ROOM_ID));
            Assert.True(room.EndDate > DateTimeOffset.UtcNow.AddMinutes(20));
            SetUserContext(ContextUser);
            await Hub.JoinRoom(ROOM_ID);
            Assert.Null(room.EndDate);
            Assert.Null(room.Host);
        }

        [Fact]
        public async Task RefereeAssociationCannotBypassManagedRoomControls()
        {
            await setup();
            var referee = new RefereeHub(DatabaseFactory.Object, LoggerFactory.Object, LegacyIO.Object, RoomController,
                EventDispatcher, RefereeStates, UserStates, new ChatFilters(DatabaseFactory.Object), RulesetManager)
            {
                Context = ContextUser.Object
            };
            await referee.OnConnectedAsync();
            using (var state = await RefereeStates.GetForUse(USER_ID))
                state.Item!.AssociateWithRoom(ROOM_ID);
            await Assert.ThrowsAsync<InvalidStateException>(() => referee.StartMatch(ROOM_ID, new StartGameplayRequest()));
            await Assert.ThrowsAsync<InvalidStateException>(() => referee.CloseRoom(ROOM_ID));
            await Assert.ThrowsAsync<InvalidStateException>(() => referee.KickPlayer(ROOM_ID, USER_ID_2));
            Assert.Equal(2, room.Users.Count);
        }

        private sealed class FakeInterop : ISomsaiInteropClient
        {
            public int[][] Roster = [];
            public long ItemId;
            public string Stage = "ready";
            public bool FailStartOnce;
            public bool FailCompletionOnce;
            public TaskCompletionSource? StartGate;
            public readonly TaskCompletionSource StartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly List<string> Events = [];
            public readonly List<long> Items = [];

            private SomsaiRoomState snapshot() => new SomsaiRoomState
            {
                Managed = true, Roster = Roster, PlaylistItemId = ItemId, Stage = Stage, StartAllowed = Stage == "ready"
            };
            public Task<SomsaiRoomState> GetRoom(long roomId) => Task.FromResult(snapshot());
            public async Task<SomsaiRoomState> SendEvent(long roomId, string kind, long itemId, int[] connected, int[] ready, int[] available)
            {
                Events.Add(kind);
                Items.Add(itemId);
                if (kind == "started")
                {
                    Stage = "playing";
                    StartEntered.TrySetResult();
                    if (StartGate != null)
                        await StartGate.Task;
                    if (FailStartOnce)
                    {
                        FailStartOnce = false;
                        throw new HttpRequestException("The app committed the start, but its response was lost.");
                    }
                }
                if (kind == "completed")
                {
                    if (FailCompletionOnce)
                    {
                        FailCompletionOnce = false;
                        throw new HttpRequestException("App unavailable.");
                    }
                    Stage = "results";
                }
                if (kind == "aborted")
                    Stage = "cancelled";
                return snapshot();
            }
        }
    }
}
