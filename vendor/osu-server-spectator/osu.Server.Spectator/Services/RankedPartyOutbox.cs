// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace osu.Server.Spectator.Services
{
    /// <summary>Release this spectator's outstanding party reservations after a restart.</summary>
    public sealed class RankedPartyOutbox : IDisposable
    {
        private readonly string directory;
        private readonly FileStream ownerLock;

        public RankedPartyOutbox(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            // A second live host must never release the first host's reservations.
            ownerLock = new FileStream(Path.Combine(directory, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public IEnumerable<Entry> Load()
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
                yield return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))
                             ?? throw new InvalidDataException("Invalid Ranked party outbox entry.");
        }

        public void Save(RankedPartyReservation reservation, int poolId)
        {
            string destination = pathFor(reservation.ReservationId);
            using (var stream = new FileStream(destination + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Entry(poolId, reservation));
                stream.Flush(flushToDisk: true);
            }
            File.Move(destination + ".tmp", destination, overwrite: true);
        }

        public void Remove(string reservationId) => File.Delete(pathFor(reservationId));
        public void Dispose() => ownerLock.Dispose();
        private string pathFor(string id) => Path.Combine(directory, $"{Guid.Parse(id):D}.json");
        public record Entry(int PoolId, RankedPartyReservation Reservation);
    }
}
