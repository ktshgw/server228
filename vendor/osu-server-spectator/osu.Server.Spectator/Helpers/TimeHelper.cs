// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator.Helpers
{
    public static class TimeHelper
    {
        // osu! was released at 2007-09-16, so we use the day before as a base to ensure all timestamps are valid.
        // Mapping to seconds since this date allows us to store the value in an int32 over 2038.
        private static readonly long BaseTimestamp =
            new DateTimeOffset(2007, 9, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static int ToMappedInt(long realTimestamp)
        {
            long mapped = realTimestamp - BaseTimestamp + int.MinValue;
            if (mapped < int.MinValue || mapped > int.MaxValue)
                throw new OverflowException("Mapped timestamp out of range");
            return (int)mapped;
        }

        public static long FromMappedInt(int stored)
        {
            return (long)stored - int.MinValue + BaseTimestamp;
        }

        public static DateTimeOffset ToDateTimeOffset(int stored)
        {
            long real = FromMappedInt(stored);
            return DateTimeOffset.FromUnixTimeSeconds(real).UtcDateTime;
        }

        public static int ToMappedInt(DateTimeOffset time) => ToMappedInt(time.ToUnixTimeSeconds());
    }
}
