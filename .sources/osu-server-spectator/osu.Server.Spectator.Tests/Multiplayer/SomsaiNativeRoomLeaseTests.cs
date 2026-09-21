using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer
{
    public class SomsaiNativeRoomLeaseTests
    {
        private readonly Mock<ISomsaiNativeRoomInterop> interop = new Mock<ISomsaiNativeRoomInterop>();
        private readonly FakeClock clock = new FakeClock();
        private readonly SomsaiNativeRoomLeaseService service;

        public SomsaiNativeRoomLeaseTests()
        {
            service = new SomsaiNativeRoomLeaseService(interop.Object, Mock.Of<ILogger<SomsaiNativeRoomLeaseService>>(), clock);
        }

        [Fact]
        public async Task ReservedPlayerCannotClaimOrdinaryRoom()
        {
            interop.Setup(api => api.ClaimNativeRoom(80, 42)).ThrowsAsync(new HttpRequestException("Already reserved", null, HttpStatusCode.Conflict));
            await Assert.ThrowsAsync<HttpRequestException>(() => service.Claim(80, 42, () => Task.CompletedTask));
            await service.RefreshOnce(force: true);
            interop.Verify(api => api.ClaimNativeRoom(80, 42), Times.Once);
        }

        [Fact]
        public async Task ReleaseFailureRetriesWithoutRenewingAbandonedRoom()
        {
            await service.Claim(80, 42, () => Task.CompletedTask);
            interop.SetupSequence(api => api.ReleaseNativeRoom(80, 42))
                   .ThrowsAsync(new HttpRequestException("Offline")).Returns(Task.CompletedTask);
            await service.Release(80, 42);
            await service.RefreshOnce(force: true);
            await service.RefreshOnce(force: true);
            interop.Verify(api => api.ReleaseNativeRoom(80, 42), Times.Exactly(2));
            interop.Verify(api => api.ClaimNativeRoom(80, 42), Times.Once);
        }

        [Fact]
        public async Task ConflictingRenewalLeavesRoomAndCanReleaseWithoutDeadlock()
        {
            int exits = 0;
            await service.Claim(80, 42, async () =>
            {
                exits++;
                await service.Release(80, 42);
            });
            interop.Setup(api => api.ClaimNativeRoom(80, 42)).ThrowsAsync(new HttpRequestException("New match reserved", null, HttpStatusCode.Conflict));
            await service.RefreshOnce(force: true).WaitAsync(TimeSpan.FromSeconds(2));
            await service.RefreshOnce(force: true);
            Assert.Equal(1, exits);
            interop.Verify(api => api.ReleaseNativeRoom(80, 42), Times.Once);
        }

        [Fact]
        public async Task OutageLeavesBeforeTheFortyFiveSecondServerLeaseExpires()
        {
            int exits = 0;
            await service.Claim(80, 42, async () =>
            {
                exits++;
                await service.Release(80, 42);
            });
            interop.Setup(api => api.ClaimNativeRoom(80, 42)).ThrowsAsync(new HttpRequestException("Offline"));
            clock.Now = clock.Now.AddSeconds(30);
            await service.RefreshOnce();
            Assert.Equal(0, exits);
            clock.Now = clock.Now.AddSeconds(6);
            await service.RefreshOnce();
            Assert.Equal(1, exits);
        }

        private sealed class FakeClock : TimeProvider
        {
            public DateTimeOffset Now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
