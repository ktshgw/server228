// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace osu.Server.Spectator.Services
{
    /// <summary>Pending penalty writes retained on the spectator state volume.</summary>
    public class RankedDodgeOutbox
    {
        private readonly string directory;

        public RankedDodgeOutbox(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
        }

        public Dictionary<long, int> Load()
        {
            var result = new Dictionary<long, int>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                Entry entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))
                              ?? throw new InvalidDataException("Invalid pending Ranked penalty.");
                if (entry.RoomId <= 0 || entry.UserId <= 0)
                    throw new InvalidDataException("Invalid pending Ranked penalty identifiers.");
                result.Add(entry.RoomId, entry.UserId);
            }
            return result;
        }

        public void Save(long roomId, int userId)
        {
            string destination = pathFor(roomId);
            string temporary = destination + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Entry(roomId, userId));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }

        public void Remove(long roomId) => File.Delete(pathFor(roomId));

        private string pathFor(long roomId) => Path.Combine(directory, $"{roomId}.json");

        private record Entry(long RoomId, int UserId);
    }
}
