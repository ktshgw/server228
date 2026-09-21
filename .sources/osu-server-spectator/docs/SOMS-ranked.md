# SOMS! Ranked and SOMSAI

Ordinary Ranked 1v1 keeps its existing pools and ratings. Active `ranked_play`
pools with `lobby_size=4` run 2v2 and use their own per-pool player ratings.
Every team begins with 2,000,000 shared life. Four private five-card hands are
dealt from a common deck; the pool must contain at least twenty playable cards.
Turns follow A1, B1, A2, B2. Each round compares the sum of the two team scores;
damage applies once to the losing team's shared life. The two-team rating
calculation updates all four players when the match ends.

Before a player's first Replace confirmation, leaving cancels ordinary Ranked
for everyone without changing ratings and applies the existing dodge progression
only to the leaver. After confirmation, leaving forfeits the leaver's whole team.
Duels retain their existing unranked behavior and do not receive dodge penalties.

`MatchmakingJoinQueue` routes Ranked through `SomsRankedQueue(poolId)`. A solo
player reserves one participant; a 2v2 party captain reserves both members.
Party members must be connected and eligible together. Declining, disconnecting,
or cancelling removes the whole party; only the player who declined or timed out
receives an invitation penalty. Search never splits a party between teams.

The existing MessagePack ranked state is unchanged. For a team match, each
member's `Life` repeats its team's shared life. The JSON-string hub extension
`SomsRankedTeamState(roomId)` exposes `team_size`, `max_life`, `teams` (each with
`id`, `user_ids`, `life`), `turn_order`, `winning_team_id`, and `cancelled`.
Only a player currently associated with that room can request it.

Party reservation endpoints are protected by the existing `/_lio` HMAC. Reserve
requests carry a UUID `request_id`, reused across HTTP retries. The app atomically
excludes overlapping SOMSAI, ordinary Ranked, and ordinary multiplayer activity.
Ranked reservations renew every sixty seconds and once again immediately before
room creation. They release on queue cancellation or match completion. Owned
reservation IDs are persisted next to `SOMS_DODGE_OUTBOX` and released after a
spectator restart; an exclusive file lock prevents a second host from releasing
a running host's reservations.

Ordinary multiplayer rooms claim each participant through the same coordinator
before joining. Claims renew every ten seconds, expire after forty-five seconds,
and release on leave. Conflicts immediately remove the player from that ordinary
room; an app outage removes them after thirty-five seconds without a confirmed
renewal. Managed Ranked/SOMSAI rooms use their existing reservations instead.

SOMSAI rooms use the locked native TeamVersus transport for 1v1 through 4v4.
The app owns the roster, draft, maps, and results. Native start intentions and
completion events retry with the same playlist item; network calls never hold
the room lock. A roster change during start acknowledgement or a native start
failure cancels through the app without awarding rating. Referee controls cannot
change these managed rooms. Empty tournament rooms retain a reconnect window.

Relevant regression tests are `RankedTeamPlayTests`, `RankedTeamQueueTests`,
`RankedPartyQueueTests`, `SomsaiMatchTests`, `SomsaiNativeRoomLeaseTests`, and
`SharedInteropTests`. `Spectator.Interop.Check` performs read-only live DTO,
signature, pool, and catalogue checks against the built spectator assembly.
