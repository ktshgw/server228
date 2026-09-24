// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using osu.Server.Spectator.Extensions;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class HubCallerContextExtensionsTests
    {
        [Fact]
        public void TestGetUserIdFromJwt()
        {
            var httpContext = new DefaultHttpContext();
            var token = new JwtSecurityToken(claims: [new Claim("sub", "123")]);
            httpContext.Request.Headers.Authorization = $"Bearer {new JwtSecurityTokenHandler().WriteToken(token)}";

            var feature = new Mock<IHttpContextFeature>();
            feature.SetupGet(f => f.HttpContext).Returns(httpContext);

            var context = new Mock<HubCallerContext>();
            context.Setup(c => c.Features.Get<IHttpContextFeature>()).Returns(feature.Object);

            Assert.Equal(123, context.Object.GetUserId());
        }

        [Fact]
        public void TestGetUserIdFallsBackToUserIdentifier()
        {
            var context = new Mock<HubCallerContext>();
            context.SetupGet(c => c.UserIdentifier).Returns("456");

            Assert.Equal(456, context.Object.GetUserId());
        }

        [Fact]
        public void TestGetUserIdRejectsMissingIdentity()
        {
            var context = new Mock<HubCallerContext>();
            Assert.Throws<InvalidOperationException>(() => context.Object.GetUserId());
        }
    }
}
