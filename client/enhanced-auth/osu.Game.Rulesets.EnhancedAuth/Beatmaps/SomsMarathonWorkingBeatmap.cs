#nullable enable
using System.IO;
using System.Security.Cryptography;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Objects;
using osu.Game.Skinning;
using System;
using System.Linq;

namespace osu.Game.Rulesets.EnhancedAuth.Beatmaps;

public sealed class SomsMarathonWorkingBeatmap : WorkingBeatmap, IDisposable
{
    internal static SomsMarathonWorkingBeatmap? Active { get; private set; }
    private readonly IBeatmap content;
    private readonly WorkingBeatmap background;
    private readonly ITrackStore tracks;
    private readonly NativeStorage storage;
    private readonly IStorageResourceProvider resources;
    private MarathonSkin? marathonSkin;

    public static SomsMarathonWorkingBeatmap Open(SomsMarathonCompilation compilation, AudioManager audio, IStorageResourceProvider resources)
    {
        string path = Path.Combine(compilation.Directory, "marathon.osu");
        using var stream = File.OpenRead(path);
        using var reader = new LineBufferedReader(stream);
        var beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        beatmap.BeatmapInfo.MD5Hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        beatmap.BeatmapInfo.OnlineID = -1;
        beatmap.BeatmapInfo.Ruleset = compilation.Background.BeatmapInfo.Ruleset;
        beatmap.BeatmapInfo.Length = beatmap.HitObjects.Max(hit => hit.GetEndTime());
        return Active = new SomsMarathonWorkingBeatmap(beatmap, compilation.Background, compilation.Directory, audio, resources);
    }

    private SomsMarathonWorkingBeatmap(IBeatmap beatmap, WorkingBeatmap background, string directory, AudioManager audio, IStorageResourceProvider resources)
        : base(beatmap.BeatmapInfo, audio)
    {
        content = beatmap;
        this.background = background;
        this.resources = resources;
        storage = new NativeStorage(directory);
        tracks = audio.GetTrackStore(new StorageBackedResourceStore(storage));
    }

    protected override IBeatmap GetBeatmap() => content;
    public override Texture GetBackground() => background.GetBackground();
    protected override Track GetBeatmapTrack() => tracks.Get("audio.wav");
    protected override ISkin GetSkin() => marathonSkin = new MarathonSkin(resources, new StorageBackedResourceStore(storage));
    public override Stream GetStream(string storagePath) => storage.GetStream(storagePath);
    public void Dispose() { if (ReferenceEquals(Active, this)) Active = null; CancelAsyncLoad(); tracks.Dispose(); marathonSkin?.Dispose(); }

    private sealed class MarathonSkin : LegacySkin
    {
        protected override bool UseCustomSampleBanks => true;
        protected override bool AllowManiaConfigLookups => false;
        public MarathonSkin(IStorageResourceProvider resources, IResourceStore<byte[]> store)
            : base(new SkinInfo { Name = "Marathon samples" }, resources, store, "marathon.osu")
        {
        }
        public override IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup)
            => lookup is GlobalSkinColours.ComboColours or SkinConfiguration.LegacySetting.Version ? null : base.GetConfig<TLookup, TValue>(lookup);
    }
}
