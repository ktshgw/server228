(() => {
    "use strict";

    const metadata = document.querySelector('meta[name="soms-beatmapset-id"]');
    const beatmapsetId = Number(metadata?.content || 0);
    if (!Number.isSafeInteger(beatmapsetId) || beatmapsetId <= 0) {
        location.replace("/site/#beatmaps");
        return;
    }

    const difficultyMatch = location.hash.match(/^#(?:osu|taiko|fruits|mania)\/(\d+)$/);
    const difficultyId = Number(difficultyMatch?.[1] || 0);
    const route = Number.isSafeInteger(difficultyId) && difficultyId > 0
        ? `/site/#beatmap/${beatmapsetId}/${difficultyId}`
        : `/site/#beatmap/${beatmapsetId}`;
    location.replace(route);
})();
