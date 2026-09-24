#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using ManagedBass;
using osu.Game.Beatmaps;

namespace osu.Game.Rulesets.EnhancedAuth.Beatmaps;

// Decode with the audio library already shipped by lazer. No external programs or codecs.
public sealed class SomsMarathonAudio : IDisposable
{
    public const int Frequency = 44100;
    private readonly FileStream output;
    private long frames;
    public SomsMarathonAudio(string path)
    {
        output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        output.Write(new byte[44]);
    }

    public void Append(WorkingBeatmap map, int startMs, int endMs, int crossfadeMs, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(output.Name)!, "decode" + Path.GetExtension(map.Metadata.AudioFile));
        try
        {
            using (var input = SomsMarathonCompiler.OpenFile(map, map.Metadata.AudioFile))
            using (var copy = File.Create(temporary)) input.CopyTo(copy);
            float[] samples = Decode(temporary, startMs, endMs, token);
            AppendPcm(samples, crossfadeMs, token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static float[] Decode(string path, int startMs, int endMs, CancellationToken token)
    {
        int channel = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
        if (channel == 0) throw new InvalidOperationException("Не удалось прочитать аудио карты: " + Bass.LastError);
        try
        {
            if (!Bass.ChannelGetInfo(channel, out var info) || info.Channels is < 1 or > 8)
                throw new InvalidOperationException("Неподдерживаемый формат аудио.");
            double lengthMs = Bass.ChannelBytes2Seconds(channel, Bass.ChannelGetLength(channel)) * 1000;
            if (startMs >= lengthMs || endMs <= startMs || endMs - startMs > 183000)
                throw new InvalidOperationException("Фрагмент находится за пределами аудио.");
            int outputFrames = (int)Math.Round((endMs - startMs) * Frequency / 1000.0);
            var result = new float[outputFrames * 2];
            int sourceFrames = (int)Math.Ceiling((Math.Min(endMs, lengthMs) - Math.Max(0, startMs)) * info.Frequency / 1000.0) + 2;
            var source = new float[Math.Max(0, sourceFrames) * info.Channels];
            if (!Bass.ChannelSetPosition(channel, Bass.ChannelSeconds2Bytes(channel, Math.Max(0, startMs) / 1000.0)))
                throw new InvalidOperationException("Не удалось перейти к фрагменту аудио.");
            var block = new float[16384];
            int read = 0;
            while (read < source.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = Bass.ChannelGetData(channel, block, Math.Min(block.Length, source.Length - read) * sizeof(float));
                if (count <= 0) break;
                count /= sizeof(float);
                Array.Copy(block, 0, source, read, count);
                read += count;
            }
            int availableFrames = read / info.Channels;
            int padding = (int)Math.Round(Math.Max(0, -startMs) * Frequency / 1000.0);
            double squared = 0;
            float peak = 0;
            for (int i = padding; i < outputFrames; i++)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                double position = (i - padding) * (double)info.Frequency / Frequency;
                int left = (int)position;
                if (left >= availableFrames) break;
                int right = Math.Min(left + 1, availableFrames - 1);
                float fraction = (float)(position - left);
                for (int c = 0; c < 2; c++)
                {
                    int sourceChannel = Math.Min(c, info.Channels - 1);
                    float a = source[left * info.Channels + sourceChannel];
                    float b = source[right * info.Channels + sourceChannel];
                    float value = float.IsFinite(a) && float.IsFinite(b) ? a + (b - a) * fraction : 0;
                    result[i * 2 + c] = value;
                    squared += value * value;
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }
            // Keep perceived loudness similar without boosting quiet sections excessively.
            double rms = Math.Sqrt(squared / Math.Max(1, result.Length));
            float gain = rms > .0001 && peak > .0001 ? (float)Math.Min(Math.Clamp(.16 / rms, .25, 2), .95 / peak) : 1;
            for (int i = 0; i < result.Length; i++) result[i] *= gain;
            return result;
        }
        finally { Bass.StreamFree(channel); }
    }

    public void AppendPcm(float[] samples, int crossfadeMs, CancellationToken token)
    {
        int count = samples.Length / 2;
        int overlap = (int)Math.Min(Math.Min(frames, count), crossfadeMs * Frequency / 1000L);
        long beginning = frames - overlap;
        var old = new byte[overlap * 4];
        output.Position = 44 + beginning * 4;
        if (old.Length > 0) output.ReadExactly(old);
        output.Position = 44 + beginning * 4;
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < count; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            for (int c = 0; c < 2; c++)
            {
                double sample = samples[i * 2 + c];
                if (i < overlap)
                {
                    double mix = i / (double)Math.Max(1, overlap - 1) * Math.PI / 2;
                    double previous = BitConverter.ToInt16(old, i * 4 + c * 2) / 32768.0;
                    sample = previous * Math.Cos(mix) + sample * Math.Sin(mix);
                }
                else if (frames == 0 && i < Frequency / 4) sample *= i / (Frequency / 4.0);
                writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
            }
        }
        frames = beginning + count;
    }

    public void Finish()
    {
        // Fade the compilation's tail; transitions already have equal-power fades.
        int tail = (int)Math.Min(frames, Frequency / 2);
        output.Position = 44 + (frames - tail) * 4;
        var data = new byte[tail * 4];
        output.ReadExactly(data);
        output.Position = 44 + (frames - tail) * 4;
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < tail; i++)
            for (int c = 0; c < 2; c++) writer.Write((short)(BitConverter.ToInt16(data, i * 4 + c * 2) * (1 - i / (double)Math.Max(1, tail - 1))));
        output.Position = 0;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((int)(frames * 4 + 36));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)2); writer.Write(Frequency); writer.Write(Frequency * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((int)(frames * 4));
        output.Flush();
    }

    public void Dispose() => output.Dispose();
}
