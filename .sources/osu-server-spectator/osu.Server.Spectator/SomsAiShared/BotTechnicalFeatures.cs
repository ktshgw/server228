#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SomsAi.Shared;

public sealed class BotTechnicalFeatures
{
    public double Rhythm { get; private set; }
    public double Angles { get; private set; }
    public double SliderTech { get; private set; }

    public static BotTechnicalFeatures? TryParse(string raw)
    {
        try { return Parse(raw); }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException or ArgumentException)
        {
            return null; // Optional style analysis must not prevent native gameplay.
        }
    }

    // Same chart descriptors as somsai_bot_tech.py. Mirroring and constant rate
    // changes preserve these patterns; actual AR/CS/BPM and stars carry the mods.
    public static BotTechnicalFeatures Parse(string raw)
    {
        var timing = new List<(double Time, double Value)>();
        var hits = new List<string[]>();
        string section = "";
        double multiplier = 1.4;
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('[')) { section = line; continue; }
            if (section == "[Difficulty]" && line.StartsWith("SliderMultiplier:"))
                multiplier = number(line.Split(':')[1]);
            else if (section == "[TimingPoints]" && line.Contains(','))
            {
                var parts = line.Split(',');
                timing.Add((number(parts[0]), number(parts[1])));
            }
            else if (section == "[HitObjects]" && line.Contains(','))
            {
                var parts = line.Split(',');
                if ((int.Parse(parts[3], CultureInfo.InvariantCulture) & 8) == 0) hits.Add(parts);
            }
        }
        if (hits.Count < 3) return new BotTechnicalFeatures();
        hits = hits.OrderBy(p => number(p[2])).ToList();
        timing.Sort((a, b) => a.Time.CompareTo(b.Time));
        var times = hits.Select(p => number(p[2])).ToArray();
        var points = hits.Select(p => (X: number(p[0]), Y: number(p[1]))).ToArray();
        var deltas = times.Zip(times.Skip(1), (a, b) => b - a).Where(d => d >= 25 && d <= 1000).ToArray();
        var distances = points.Zip(points.Skip(1), (a, b) => length(b.X - a.X, b.Y - a.Y)).Where(d => d > 1).ToArray();
        var angles = turns(points);
        double angleVariation = mean(angles.Zip(angles.Skip(1), (a, b) => Math.Abs(b - a) / Math.PI));
        var shapes = new List<double>();
        var speeds = new List<double>();
        var repeats = new List<double>();
        int cursor = 0;
        double beatLength = 500, sv = 1;
        foreach (var parts in hits)
        {
            while (cursor < timing.Count && timing[cursor].Time <= number(parts[2]))
            {
                double value = timing[cursor++].Value;
                if (value > 0) { beatLength = value; sv = 1; }
                else if (value < 0) sv = Math.Clamp(-100 / value, .1, 10);
            }
            if ((int.Parse(parts[3], CultureInfo.InvariantCulture) & 2) == 0 || parts.Length < 8) continue;
            var path = new List<(double X, double Y)> { (number(parts[0]), number(parts[1])) };
            foreach (var token in parts[5].Split('|').Skip(1).Where(p => p.Contains(':')))
            {
                var xy = token.Split(':');
                path.Add((number(xy[0]), number(xy[1])));
            }
            shapes.Add(mean(turns(path)) / Math.PI);
            speeds.Add(multiplier * 100 * sv / beatLength);
            repeats.Add(Math.Clamp((number(parts[6]) - 1) / 3, 0, 1));
        }
        return new BotTechnicalFeatures
        {
            Rhythm = variation(deltas),
            Angles = Math.Min(1, angleVariation * 1.5 + variation(distances) * .3),
            SliderTech = Math.Min(1, mean(shapes) * .45 + variation(speeds) * .4 + mean(repeats) * .15),
        };
    }

    private static double number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static double length(double x, double y) => Math.Sqrt(x * x + y * y);
    private static double mean(IEnumerable<double> values) => values.DefaultIfEmpty(0).Average();
    private static double variation(IEnumerable<double> values) => mean(values.Zip(values.Skip(1), (a, b) => (a, b))
        .Where(p => Math.Min(p.a, p.b) > 0).Select(p => Math.Min(1, Math.Abs(Math.Log2(p.b / p.a)))));
    private static double[] turns(IReadOnlyList<(double X, double Y)> points)
    {
        var result = new List<double>();
        for (int i = 2; i < points.Count; i++)
        {
            var a = points[i - 2]; var b = points[i - 1]; var c = points[i];
            double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - b.X, vy = c.Y - b.Y;
            double size = length(ux, uy) * length(vx, vy);
            if (size > 1) result.Add(Math.Acos(Math.Clamp((ux * vx + uy * vy) / size, -1, 1)));
        }
        return result.ToArray();
    }
}
