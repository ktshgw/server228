#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;

namespace osu.Game.Rulesets.EnhancedAuth.Beatmaps;

public sealed record SomsMarathonCompilation(string Directory, WorkingBeatmap Background, IReadOnlyList<(double Time, string Title)> Songs);

public static class SomsMarathonCompiler
{
    public const int Lead = 1500;
    public const int Crossfade = 1000;
    private static readonly CultureInfo culture = CultureInfo.InvariantCulture;

    public static SomsMarathonSegment Suggest(WorkingBeatmap working)
    {
        var playable = working.GetPlayableBeatmap(working.BeatmapInfo.Ruleset, Array.Empty<Mod>());
        var objects = playable.HitObjects;
        if (objects.Count == 0) throw new InvalidOperationException("В карте нет объектов.");
        double last = objects.Max(hit => hit.GetEndTime());
        // Consecutive effect points can change other effects without ending Kiai.
        var points = playable.ControlPointInfo.EffectPoints.Where((point, index) => index == 0
            || point.KiaiMode != playable.ControlPointInfo.EffectPoints[index - 1].KiaiMode).ToArray();
        var kiai = points.Select((point, index) => new
        {
            Start = point.Time,
            End = index + 1 < points.Length ? Math.Min(last + 1, points[index + 1].Time) : last + 1,
            point.KiaiMode,
        }).Where(part => part.KiaiMode && part.End - part.Start >= 5000)
            .OrderByDescending(part => Math.Min(part.End - part.Start, 45000)).FirstOrDefault();
        double start;
        double end;
        if (kiai != null) { start = Math.Max(0, kiai.Start); end = Math.Min(kiai.End, start + 45000); }
        else
        {
            // No Kiai: use a busy 30-second section, preferring the song preview on ties.
            double preview = working.Metadata.PreviewTime;
            start = 0;
            int left = 0, right = 0, best = -1;
            foreach (double time in objects.Where((_, index) => index % 16 == 0)
                         .Select(hit => Math.Max(0, Math.Min(hit.StartTime, last - 30000))))
            {
                while (left < objects.Count && objects[left].StartTime < time) left++;
                while (right < objects.Count && objects[right].StartTime < time + 30000) right++;
                int count = right - left;
                if (count > best || count == best && Math.Abs(time - preview) < Math.Abs(start - preview))
                { best = count; start = time; }
            }
            end = Math.Min(last + 1, start + 30000);
        }
        if (end - start < 5000) { start = Math.Max(0, last - 5000); end = start + 5000; }
        return new SomsMarathonSegment
        {
            BeatmapId = working.BeatmapInfo.OnlineID,
            BeatmapSetId = working.BeatmapSetInfo.OnlineID,
            Checksum = working.BeatmapInfo.MD5Hash,
            Title = $"{working.Metadata.Artist} — {working.Metadata.Title} [{working.BeatmapInfo.DifficultyName}]",
            StartMs = (int)Math.Floor(start), EndMs = (int)Math.Ceiling(end),
        };
    }

    public static SomsMarathonCompilation Compile(SomsMarathonDefinition definition, IReadOnlyList<WorkingBeatmap> maps,
        string cacheRoot, Action<string> progress, CancellationToken token)
    {
        if (definition.CompilerVersion != 1 || maps.Count is < 2 or > 20 || maps.Count != definition.Segments.Count)
            throw new InvalidOperationException("Нужно выбрать от 2 до 20 карт.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        { definition.CompilerVersion, definition.RulesetId, definition.Name, definition.Segments,
            Revisions = maps.Select(map => map.BeatmapInfo.MD5Hash).ToArray() })))).ToLowerInvariant();
        string directory = Path.Combine(cacheRoot, "compiled-" + key);
        Directory.CreateDirectory(directory);
        var songs = new List<(double Time, string Title)>();
        double position = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            songs.Add((position, definition.Segments[i].Title));
            position += definition.Segments[i].EndMs - definition.Segments[i].StartMs + Lead * 2 - Crossfade;
        }
        if (File.Exists(Path.Combine(directory, "complete")))
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
            trimCache(cacheRoot, directory);
            return new(directory, maps[0], songs);
        }
        var timing = new List<string>();
        var hits = new List<string>();
        var first = new Document(readMap(maps[0]));
        double sliderMultiplier = first.Number("Difficulty", "SliderMultiplier", 1.4);
        int keys = (int)first.Number("Difficulty", "CircleSize", 4);
        using (var wav = new SomsMarathonAudio(Path.Combine(directory, "audio.wav")))
        {
            for (int i = 0; i < maps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var segment = definition.Segments[i];
                var map = maps[i];
                if (segment.BeatmapId > 0 && map.BeatmapInfo.OnlineID != segment.BeatmapId)
                    throw new InvalidOperationException("Выбрана другая сложность карты: " + segment.Title);
                if (segment.EndMs - segment.StartMs is < 5000 or > 180000 || segment.StartMs < 0)
                    throw new InvalidOperationException("Фрагмент должен длиться от 5 до 180 секунд.");
                progress($"Собираем {i + 1}/{maps.Count}: {segment.Title}");
                var document = new Document(readMap(map));
                if ((int)document.Number("General", "Mode", 0) != definition.RulesetId)
                    throw new InvalidOperationException("В одном марафоне должны быть карты одного режима.");
                if (definition.RulesetId == 3 && (int)document.Number("Difficulty", "CircleSize", 4) != keys)
                    throw new InvalidOperationException("Для osu!mania выберите карты с одинаковым количеством клавиш.");
                double shift = songs[i].Time + Lead - segment.StartMs;
                int sampleBase = (i + 1) * 10000;
                var playable = map.GetPlayableBeatmap(map.BeatmapInfo.Ruleset, Array.Empty<Mod>());
                var includedTimes = playable.HitObjects.Where(hit => hit.StartTime >= segment.StartMs && hit.GetEndTime() <= segment.EndMs)
                    .Select(hit => Math.Round(hit.StartTime, 2)).ToHashSet();
                int before = hits.Count;
                foreach (var line in document.Section("HitObjects"))
                {
                    string[] parts = line.Split(',');
                    if (parts.Length < 5) continue;
                    double time = double.Parse(parts[2], culture) + document.Offset;
                    if (!includedTimes.Contains(Math.Round(time, 2))) continue;
                    int type = int.Parse(parts[3], culture);
                    parts[2] = number(time + shift);
                    int samplePosition;
                    if ((type & 2) != 0) samplePosition = 10;
                    else if ((type & 8) != 0)
                    {
                        parts[5] = number(double.Parse(parts[5], culture) + document.Offset + shift);
                        samplePosition = 6;
                    }
                    else if ((type & 128) != 0)
                    {
                        var hold = parts[5].Split(':');
                        hold[0] = number(double.Parse(hold[0], culture) + document.Offset + shift);
                        var sample = remapSample(hold.Skip(1).ToArray(), sampleBase, i);
                        parts[5] = hold[0] + ":" + string.Join(':', sample);
                        samplePosition = -1;
                    }
                    else samplePosition = 5;
                    if (samplePosition >= 0 && parts.Length > samplePosition)
                        parts[samplePosition] = string.Join(':', remapSample(parts[samplePosition].Split(':'), sampleBase, i));
                    hits.Add(string.Join(',', parts));
                }
                if (before == hits.Count) throw new InvalidOperationException("В выбранном фрагменте нет целых объектов: " + segment.Title);
                timing.AddRange(document.Timing(segment.StartMs, segment.EndMs, shift, songs[i].Time,
                    document.Number("Difficulty", "SliderMultiplier", 1.4) / sliderMultiplier, sampleBase));
                copySamples(map, directory, i, sampleBase, token);
                wav.Append(map, segment.StartMs - Lead, segment.EndMs + Lead, i == 0 ? 0 : Crossfade, token);
            }
            wav.Finish();
        }
        string safeName = definition.Name.Replace('\n', ' ').Replace('\r', ' ');
        var output = new StringBuilder("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nAudioLeadIn: 0\nPreviewTime: 1500\nSampleSet: Normal\nStackLeniency: 0.7\nMode: " + definition.RulesetId);
        output.Append("\n\n[Metadata]\nTitle:" + safeName + "\nArtist:Various Artists\nCreator:SOMS!\nVersion:Songs compilation\nBeatmapID:0\nBeatmapSetID:-1\n\n[Difficulty]\n");
        foreach (string line in first.Section("Difficulty")) output.AppendLine(line);
        output.Append("\n[TimingPoints]\n");
        foreach (string line in timing) output.AppendLine(line);
        output.Append("\n[HitObjects]\n");
        foreach (string line in hits) output.AppendLine(line);
        File.WriteAllText(Path.Combine(directory, "marathon.osu"), output.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "complete"), "1");
        trimCache(cacheRoot, directory);
        return new(directory, maps[0], songs);
    }

    private static void trimCache(string cacheRoot, string current)
    {
        // Keep the active compilation, and at most one previous compilation within 512 MiB.
        string root = Path.GetFullPath(cacheRoot);
        long bytes = Directory.EnumerateFiles(current).Sum(path => new FileInfo(path).Length);
        int retained = 1;
        foreach (var item in new DirectoryInfo(root).EnumerateDirectories("compiled-*").OrderByDescending(item => item.LastWriteTimeUtc))
        {
            if (item.FullName == Path.GetFullPath(current) || item.Parent?.FullName != root
                || item.Attributes.HasFlag(FileAttributes.ReparsePoint) || !Regex.IsMatch(item.Name, "^compiled-[0-9a-f]{64}$")) continue;
            try
            {
                long size = item.EnumerateFiles().Sum(file => file.Length);
                if (retained < 2 && bytes + size <= 512L * 1024 * 1024 && File.Exists(Path.Combine(item.FullName, "complete")))
                { retained++; bytes += size; }
                else item.Delete(true);
            }
            catch (IOException) { } // An outgoing player can still hold its audio file briefly.
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string readMap(WorkingBeatmap map)
    {
        using var stream = OpenFile(map, map.BeatmapInfo.Path ?? throw new InvalidOperationException("Файл карты не найден."));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Stream OpenFile(WorkingBeatmap map, string filename)
    {
        string path = map.BeatmapSetInfo.GetPathForFile(filename) ?? throw new InvalidOperationException("Не найден файл карты: " + filename);
        return map.GetStream(path) ?? throw new InvalidOperationException("Не удалось прочитать файл карты: " + filename);
    }

    private static string number(double value) => value.ToString("0.###", culture);
    private static string[] remapSample(string[] sample, int sampleBase, int song)
    {
        if (sample.Length > 2 && int.TryParse(sample[2], out int index) && index > 0) sample[2] = (sampleBase + index).ToString(culture);
        if (sample.Length > 4 && !string.IsNullOrWhiteSpace(sample[4])) sample[4] = "song" + song + "_" + Path.GetFileName(sample[4]);
        return sample;
    }

    private static void copySamples(WorkingBeatmap map, string directory, int song, int sampleBase, CancellationToken token)
    {
        foreach (var file in map.BeatmapSetInfo.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Filename == map.Metadata.AudioFile || !new[] { ".wav", ".mp3", ".ogg" }.Contains(Path.GetExtension(file.Filename).ToLowerInvariant())) continue;
            string basename = Path.GetFileName(file.Filename);
            var standard = Regex.Match(basename, @"^(normal|soft|drum)-(hitnormal|hitwhistle|hitfinish|hitclap|slidertick|sliderslide|sliderwhistle)(\d*)\.(wav|ogg|mp3)$", RegexOptions.IgnoreCase);
            using var input = OpenFile(map, file.Filename);
            using (var output = File.Create(Path.Combine(directory, "song" + song + "_" + basename))) input.CopyTo(output);
            if (!standard.Success) continue;
            int oldIndex = int.TryParse(standard.Groups[3].Value, out int parsed) ? parsed : 1;
            string target = $"{standard.Groups[1].Value}-{standard.Groups[2].Value}{sampleBase + oldIndex}.{standard.Groups[4].Value}";
            File.Copy(Path.Combine(directory, "song" + song + "_" + basename), Path.Combine(directory, target), true);
        }
    }

    private sealed class Document
    {
        private readonly Dictionary<string, List<string>> sections = new(StringComparer.OrdinalIgnoreCase);
        public readonly int Offset;
        public Document(string text)
        {
            string current = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("osu file format v") && int.TryParse(line[17..], out int version) && version < 5) Offset = 24;
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1]; sections.TryAdd(current, new()); }
                else if (sections.TryGetValue(current, out var lines)) lines.Add(line);
            }
        }
        public IEnumerable<string> Section(string name) => sections.TryGetValue(name, out var lines) ? lines : Array.Empty<string>();
        public double Number(string section, string key, double fallback)
        {
            string? value = Section(section).FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            return value != null && double.TryParse(value[(value.IndexOf(':') + 1)..], NumberStyles.Float, culture, out double result) ? result : fallback;
        }
        public IEnumerable<string> Timing(double start, double end, double shift, double destination, double svFactor, int sampleBase)
        {
            var points = Section("TimingPoints").Select(line => line.Split(',')).Where(parts => parts.Length >= 2)
                .OrderBy(parts => double.Parse(parts[0], culture)).ToList();
            string[] red = { "0", "500", "4", "1", "0", "100", "1", "0" };
            string[] state = (string[])red.Clone();
            double sv = 1;
            foreach (var point in points.Where(point => double.Parse(point[0], culture) + Offset <= start)) update(point);
            yield return output(red, destination, true);
            yield return output(state, destination, false);
            foreach (var point in points.Where(point => double.Parse(point[0], culture) + Offset > start && double.Parse(point[0], culture) + Offset < end))
            {
                bool isRed = double.Parse(point[1], culture) >= 0;
                update(point);
                double time = double.Parse(point[0], culture) + Offset + shift;
                if (isRed) yield return output(red, time, true);
                yield return output(state, time, false);
            }
            void update(string[] point)
            {
                var padded = new[] { "0", "500", "4", "1", "0", "100", "1", "0" };
                Array.Copy(point, padded, Math.Min(point.Length, padded.Length));
                state = padded;
                double length = double.Parse(padded[1], culture);
                if (length >= 0) { red = (string[])padded.Clone(); sv = 1; }
                else sv = -100 / length;
            }
            string output(string[] source, double time, bool uninherited)
            {
                var point = (string[])source.Clone();
                point[0] = number(time);
                point[1] = uninherited ? red[1] : number(-100 / (sv * svFactor));
                point[4] = (sampleBase + Math.Max(1, int.Parse(point[4], culture))).ToString(culture);
                point[6] = uninherited ? "1" : "0";
                return string.Join(',', point);
            }
        }
    }
}
