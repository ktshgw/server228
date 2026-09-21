#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.API;
using osu.Game.Online.Chat;
using osu.Game.Rulesets.EnhancedAuth.Online;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Native multiplayer message rendering, with an isolated, membership-checked party transport.
internal sealed partial class SomsAiPartyChat : Container
{
    [Resolved] private IAPIProvider api { get; set; } = null!;
    private readonly StandAloneChatDisplay display;
    private readonly StandAloneChatDisplay.ChatTextBox input;
    private SomsAiDataRequest? request;
    private int? partyId;
    private long lastId;
    private Channel channel = new() { Name = "Пати", MessagesLoaded = true };

    public SomsAiPartyChat()
    {
        Children = new Drawable[]
        {
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Bottom = 30 },
                Child = display = new StandAloneChatDisplay { RelativeSizeAxes = Axes.Both } },
            input = new StandAloneChatDisplay.ChatTextBox { RelativeSizeAxes = Axes.X, Height = 30,
                Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, HoldFocus = false, LengthLimit = 500,
                PlaceholderText = "Чат пати · пригласите друга" },
        };
        display.Channel.Value = channel;
        input.OnCommit += (_, _) => { if (partyId.HasValue && !string.IsNullOrWhiteSpace(input.Text)) fetch(input.Text); };
    }
    protected override void LoadComplete()
    {
        base.LoadComplete();
        Scheduler.AddDelayed(() => fetch(), 2000, true);
    }
    public void SetParty(int? id)
    {
        if (id == partyId) return;
        request?.Cancel(); request = null;
        partyId = id;
        lastId = 0;
        input.Text = "";
        input.PlaceholderText = id.HasValue ? "Введите сообщение…" : "Чат пати · пригласите друга";
        display.Channel.Value = channel = new Channel { Name = "Пати", MessagesLoaded = true };
    }
    private void fetch(string? message = null)
    {
        if (!partyId.HasValue || request != null) return;
        int? expected = partyId;
        var current = request = new SomsAiDataRequest("party/chat", message == null ? null : new JObject { ["content"] = message });
        current.Success += result => Schedule(() =>
        {
            if (request != current) return;
            request = null;
            if (expected != partyId || result.Value<int?>("party_id") != partyId) return;
            var messages = result["messages"]?.ToObject<Message[]>() ?? Array.Empty<Message>();
            var fresh = messages.Where(m => m.Id > lastId).ToArray();
            if (fresh.Length > 0)
            {
                foreach (var item in fresh)
                    if (item.Sender != null) item.Sender.AvatarUrl = new Uri(new Uri(api.Endpoints.APIUrl), item.Sender.AvatarUrl).AbsoluteUri;
                channel.AddNewMessages(fresh);
                lastId = fresh.Max(m => m.Id ?? 0);
            }
            if (message != null && input.Text == message) input.Text = "";
            input.PlaceholderText = "Введите сообщение…";
        });
        current.Failure += _ => Schedule(() => { if (request == current) { request = null; input.PlaceholderText = "Ошибка связи · попробуйте ещё раз"; } });
        api.Queue(current);
    }
    protected override void Dispose(bool isDisposing) { request?.Cancel(); base.Dispose(isDisposing); }
}
