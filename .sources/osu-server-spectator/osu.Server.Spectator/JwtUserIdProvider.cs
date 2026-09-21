// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Hubs.Referee;

namespace osu.Server.Spectator
{
    /// <remarks>
    /// This class is used by SignalR to populate <see cref="HubCallerContext.UserIdentifier"/> from a <see cref="ClaimsPrincipal"/>.
    /// The <see cref="DefaultUserIdProvider">default implementation</see> uses <see cref="ClaimTypes.NameIdentifier"/> too,
    /// and it worked fine until wanting to add support for <c>client_credentials</c> tokens in <see cref="RefereeHub"/>.
    /// Those tokens have the <see cref="ClaimTypes.NameIdentifier"/> claim with an empty value, even if they are allowed delegation,
    /// so <see cref="ConfigureJwtBearerOptions"/> adds a second non-empty copy of this claim.
    /// this implementation picks the non-empty one.
    /// </remarks>
    public class JwtUserIdProvider : IUserIdProvider
    {
      private readonly ILogger<JwtUserIdProvider> logger;

      public JwtUserIdProvider(ILoggerFactory loggerFactory)
      {
        logger = loggerFactory.CreateLogger<JwtUserIdProvider>();
      }

      public string? GetUserId(HubConnectionContext connection)
      {
        var claim = connection.User.FindFirst(claim =>
            (claim.Type == ClaimTypes.NameIdentifier || claim.Type == "sub")
            && !string.IsNullOrWhiteSpace(claim.Value));

        var userId = claim?.Value;

        if (userId == null)
        {
          var claimTypes = string.Join(", ", connection.User.Claims.Select(c => $"{c.Type}={c.Value}"));
          logger.LogWarning("JwtUserIdProvider: could not resolve user ID. Connection {ConnectionId} has claims: [{Claims}]",
              connection.ConnectionId, claimTypes);
        }

        return userId;
      }
    }
}
