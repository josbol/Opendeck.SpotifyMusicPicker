using Opendeck.SpotifyMusicPicker.Spotify;

namespace Opendeck.SpotifyMusicPicker.Picker;

/// <summary>
/// Picks a few albums from a candidate pool. Spotify's /recommendations endpoint is gone, so the pool is built
/// from things Spotify still tells us: albums you saved but have not played lately, the catalogues of your top
/// and followed artists. Deterministic per (day, reroll) so the keys do not change under your fingers.
/// </summary>
public static class Recommender
{
    public static int DailySeed(DateTimeOffset now, int reroll) => DateOnly.FromDateTime(now.LocalDateTime).DayNumber * 131 + reroll;

    /// <summary>Weighted sampling without replacement (weight = Score), preferring one album per artist.</summary>
    public static List<PickItem> Pick(IReadOnlyList<PickItem> pool, ISet<string> exclude, int count, int seed)
    {
        var rnd = new Random(seed);
        var candidates = pool.Where(p => !exclude.Contains(p.Uri)).GroupBy(p => p.Uri).Select(g => g.First()).ToList();
        var picked = new List<PickItem>();
        var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (picked.Count < count && candidates.Count > 0)
        {
            var eligible = candidates.Where(c => c.Subtitle is null || !artists.Contains(c.Subtitle)).ToList();
            if (eligible.Count == 0) eligible = candidates;
            var total = eligible.Sum(c => Math.Max(0.01, c.Score));
            var r = rnd.NextDouble() * total;
            var chosen = eligible[^1];
            foreach (var c in eligible) { r -= Math.Max(0.01, c.Score); if (r <= 0) { chosen = c; break; } }
            picked.Add(chosen);
            candidates.Remove(chosen);
            if (chosen.Subtitle is not null) artists.Add(chosen.Subtitle);
        }
        return picked;
    }
}
