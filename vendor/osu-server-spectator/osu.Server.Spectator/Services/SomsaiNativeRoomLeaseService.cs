using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace osu.Server.Spectator.Services
{
    /// <summary>Ordinary rooms and ranked queues share the app's atomic activity coordinator.</summary>
    public sealed class SomsaiNativeRoomLeaseService : BackgroundService
    {
        private sealed class Lease(long roomId, int userId, Func<Task> leave)
        {
            public readonly long RoomId = roomId;
            public readonly int UserId = userId;
            public readonly Func<Task> Leave = leave;
            public DateTimeOffset ConfirmedAt = DateTimeOffset.UtcNow;
            public DateTimeOffset NextAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
            public volatile bool Releasing;
            public readonly SemaphoreSlim Mutex = new SemaphoreSlim(1, 1);
        }

        private readonly ConcurrentDictionary<(long, int), Lease> leases = new();
        private readonly ISomsaiNativeRoomInterop interop;
        private readonly ILogger<SomsaiNativeRoomLeaseService> logger;
        private readonly TimeProvider clock;

        public SomsaiNativeRoomLeaseService(ISomsaiNativeRoomInterop interop, ILogger<SomsaiNativeRoomLeaseService> logger, TimeProvider? clock = null)
        {
            this.interop = interop;
            this.logger = logger;
            this.clock = clock ?? TimeProvider.System;
        }

        public async Task Claim(long roomId, int userId, Func<Task> leave)
        {
            await interop.ClaimNativeRoom(roomId, userId);
            leases[(roomId, userId)] = new Lease(roomId, userId, leave)
            {
                ConfirmedAt = clock.GetUtcNow(), NextAttempt = clock.GetUtcNow().AddSeconds(10)
            };
        }

        public async Task Release(long roomId, int userId)
        {
            if (!leases.TryGetValue((roomId, userId), out var lease))
                return;
            lease.Releasing = true;
            await refresh(lease);
        }

        internal Task RefreshOnce(bool force = false) => Task.WhenAll(leases.Values.Where(lease => force || lease.NextAttempt <= clock.GetUtcNow()).Select(refresh));

        private async Task refresh(Lease lease)
        {
            bool mustLeave = false;
            await lease.Mutex.WaitAsync();
            try
            {
                if (lease.Releasing)
                {
                    await interop.ReleaseNativeRoom(lease.RoomId, lease.UserId);
                    leases.TryRemove((lease.RoomId, lease.UserId), out _);
                }
                else
                {
                    await interop.ClaimNativeRoom(lease.RoomId, lease.UserId);
                    lease.ConfirmedAt = clock.GetUtcNow();
                }
                lease.NextAttempt = clock.GetUtcNow().AddSeconds(10);
            }
            catch (Exception exception)
            {
                lease.NextAttempt = clock.GetUtcNow().AddSeconds(2);
                mustLeave = !lease.Releasing && (exception is HttpRequestException { StatusCode: HttpStatusCode.Conflict }
                    || clock.GetUtcNow() - lease.ConfirmedAt >= TimeSpan.FromSeconds(35));
                logger.LogWarning(exception, "Native room {RoomId} activity claim for {UserId} failed", lease.RoomId, lease.UserId);
            }
            finally
            {
                lease.Mutex.Release();
            }
            // Never take participant/room locks under the lease mutex: Leave
            // releases the same lease and may run concurrently with disconnect.
            if (mustLeave && !lease.Releasing && leases.TryGetValue((lease.RoomId, lease.UserId), out var current) && ReferenceEquals(current, lease))
                await lease.Leave();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshOnce();
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Native room claim cleanup will retry");
                }
            }
        }
    }
}
