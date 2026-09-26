/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Prompt and parser for the AI release ranker: the gate's shortlist,
    /// enriched with everything the model needs to reason (narrator on the
    /// record, runtime-derived size band, seeders/leechers, format, age),
    /// answered as rejects plus a best-first ranking with a one-line reason
    /// each. The model is told the numbers; it never estimates them.
    /// </summary>
    public static class AiReleaseRankJudge
    {
        public const int MaxCandidates = AiReleaseGateJudge.MaxCandidates;

        // Runtime → plausible size band. 64 kbps mono ≈ 0.5 MB/min is the
        // thinnest real audiobook rip; 2.4 MB/min is 320 kbps. Beyond that
        // the scorer already rejects as a collection.
        public const double MinPlausibleMbPerMinute = 0.45;
        public const double MaxPlausibleMbPerMinute = 2.6;

        public sealed record CandidateInput(
            int Index,
            string Title,
            long? SizeBytes,
            int? Seeders,
            int? Leechers,
            string? Format,
            string? DownloadType,
            string? Indexer,
            int? AgeDays,
            string? Narrator);

        public sealed record TargetInput(
            string Title,
            IReadOnlyList<string> Authors,
            IReadOnlyList<string> Narrators,
            int? RuntimeMinutes,
            bool Abridged);

        public sealed record RankVerdict(int Index, string Reason);

        public sealed record RankResponse(
            IReadOnlyList<AiReleaseGateJudge.GateVerdict> Rejects,
            IReadOnlyList<RankVerdict> Ranking);

        public static (long MinBytes, long MaxBytes)? ExpectedSizeBand(int? runtimeMinutes)
        {
            if (runtimeMinutes is not > 0) return null;
            var minutes = runtimeMinutes.Value;
            return (
                (long)(minutes * MinPlausibleMbPerMinute * 1024 * 1024),
                (long)(minutes * MaxPlausibleMbPerMinute * 1024 * 1024));
        }

        public static string BuildSystemPrompt() =>
            "You rank download candidates for an audiobook manager. " +
            "Given the target audiobook and a numbered list of releases with their facts, do two things. " +
            "First, reject releases that are clearly NOT an audiobook copy of the target: music albums or discographies, " +
            "movies or TV, e-book-only packs, a different book, or a translation. " +
            "Release names use scene conventions (dots for spaces, quality tags, group suffixes) — normal, not suspicious. " +
            "A release naming the target's title and author IS the target — never reject it. " +
            "Second, rank the remaining releases best-first. Prefer, in this order: " +
            "(1) unabridged over abridged unless the target is abridged; " +
            "(2) a release that names the target's narrator; " +
            "(3) a size inside the expected band for the runtime — far above it is likely a collection or multiple books, far below it is likely abridged or incomplete; " +
            "(4) for torrents, more seeders; usenet releases have no seeders and that is fine; " +
            "(5) M4B over MP3 only as a final tiebreak. " +
            "Use only the facts given; do not guess seeders or sizes. " +
            "Each ranking reason must be short and cite the deciding fact by kind: narrator, size, seeders, edition, or format. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"reject\":[{\"index\":<number>,\"reason\":\"<short reason>\"}],\"ranking\":[{\"index\":<number>,\"reason\":\"<short reason>\"}]} " +
            "— the ranking lists every non-rejected index, best first. No prose, no markdown.";

        public static string BuildUserPrompt(TargetInput target, IReadOnlyList<CandidateInput> candidates)
        {
            var sb = new StringBuilder();
            sb.Append("Target audiobook: \"").Append(target.Title).Append('"');
            if (target.Authors.Count > 0) sb.Append(" by ").Append(string.Join(", ", target.Authors));
            if (target.Narrators.Count > 0) sb.Append(", narrated by ").Append(string.Join(", ", target.Narrators));
            if (target.Abridged) sb.Append(" (abridged edition)");
            sb.AppendLine();
            if (target.RuntimeMinutes is > 0)
            {
                sb.Append("Runtime: ").Append(target.RuntimeMinutes.Value).Append(" minutes");
                if (ExpectedSizeBand(target.RuntimeMinutes) is { } band)
                {
                    sb.Append("; expected size roughly ")
                        .Append(band.MinBytes / (1024 * 1024)).Append('–')
                        .Append(band.MaxBytes / (1024 * 1024)).Append(" MB");
                }
                sb.AppendLine();
            }

            sb.AppendLine("Candidate releases:");
            foreach (var c in candidates.Take(MaxCandidates))
            {
                sb.Append(c.Index).Append(": ").Append(c.Title);
                var facts = new List<string>();
                if (c.SizeBytes is > 0) facts.Add($"{c.SizeBytes.Value / (1024 * 1024)} MB");
                if (!string.IsNullOrWhiteSpace(c.Format)) facts.Add(c.Format!);
                var type = (c.DownloadType ?? string.Empty).ToLowerInvariant();
                if (type.Contains("torrent"))
                {
                    facts.Add($"torrent, {c.Seeders ?? 0} seeders, {c.Leechers ?? 0} leechers");
                }
                else if (type.Length > 0)
                {
                    facts.Add(type.Contains("usenet") || type.Contains("nzb") ? "usenet" : type);
                }
                if (c.AgeDays is >= 0) facts.Add($"{c.AgeDays} days old");
                if (!string.IsNullOrWhiteSpace(c.Narrator)) facts.Add($"listed narrator: {c.Narrator}");
                if (!string.IsNullOrWhiteSpace(c.Indexer)) facts.Add($"from {c.Indexer}");
                if (facts.Count > 0) sb.Append(" [").Append(string.Join("; ", facts)).Append(']');
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public static RankResponse ParseResponse(string? responseText, IReadOnlySet<int> validIndexes)
        {
            var rejects = AiReleaseGateJudge.ParseResponse(responseText, validIndexes);
            var ranking = new List<RankVerdict>();
            if (string.IsNullOrWhiteSpace(responseText)) return new RankResponse(rejects, ranking);

            var start = responseText.IndexOf('{');
            var end = responseText.LastIndexOf('}');
            if (start < 0 || end <= start) return new RankResponse(rejects, ranking);

            try
            {
                using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
                if (!doc.RootElement.TryGetProperty("ranking", out var ranked) || ranked.ValueKind != JsonValueKind.Array)
                {
                    return new RankResponse(rejects, ranking);
                }

                var rejected = rejects.Select(r => r.Index).ToHashSet();
                var seen = new HashSet<int>();
                foreach (var item in ranked.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("index", out var indexEl)
                        || indexEl.ValueKind != JsonValueKind.Number
                        || !indexEl.TryGetInt32(out var index)
                        || !validIndexes.Contains(index)
                        || rejected.Contains(index)
                        || !seen.Add(index))
                    {
                        continue;
                    }

                    var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                        ? reasonEl.GetString() ?? string.Empty
                        : string.Empty;
                    ranking.Add(new RankVerdict(index, reason));
                }
            }
            catch (JsonException)
            {
                ranking.Clear();
            }

            return new RankResponse(rejects, ranking);
        }

        // ------------------------------------------------------------------
        // Evidence: the model's preference is honoured only when its stated
        // reason is a fact the metadata can confirm.
        // ------------------------------------------------------------------

        public const string ClaimNarrator = "narrator";
        public const string ClaimSize = "size";
        public const string ClaimSeeders = "seeders";
        public const string ClaimEdition = "edition";

        private static readonly Regex NarratorWords = new(@"\bnarrat|\bread by|\bvoice", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SizeWords = new(@"\bsize\b|\bMB\b|\bruntime\b|\bcollection\b|\bincomplete\b|\bband\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeederWords = new(@"\bseed|\bpeer|\bswarm|\bleech", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EditionWords = new(@"\bunabridged\b|\babridged\b|\bedition\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UnabridgedRegex = new(@"\bunabr(?:idged)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AbridgedRegex = new(@"(?<!un)\babr(?:idged)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Which of the reason's claims about <paramref name="preferred"/>
        /// the metadata confirms, judged against the score leader
        /// <paramref name="leader"/> and the whole shortlist (for seeders).
        /// Empty means "nothing verifiable" and the preference is ignored.
        /// </summary>
        public static IReadOnlyList<string> ValidateClaims(
            string reason,
            CandidateInput preferred,
            CandidateInput leader,
            IReadOnlyList<CandidateInput> shortlist,
            TargetInput target)
        {
            var claims = new List<string>();
            reason ??= string.Empty;

            if (NarratorWords.IsMatch(reason) && target.Narrators.Count > 0)
            {
                if (NamesNarrator(preferred, target.Narrators) && !NamesNarrator(leader, target.Narrators))
                {
                    claims.Add(ClaimNarrator);
                }
            }

            if (SizeWords.IsMatch(reason) && ExpectedSizeBand(target.RuntimeMinutes) is { } band)
            {
                var preferredIn = preferred.SizeBytes is > 0 && preferred.SizeBytes >= band.MinBytes && preferred.SizeBytes <= band.MaxBytes;
                var leaderIn = leader.SizeBytes is > 0 && leader.SizeBytes >= band.MinBytes && leader.SizeBytes <= band.MaxBytes;
                if (preferredIn && !leaderIn)
                {
                    claims.Add(ClaimSize);
                }
            }

            if (SeederWords.IsMatch(reason))
            {
                var preferredSeeders = preferred.Seeders ?? 0;
                var leaderSeeders = leader.Seeders ?? 0;
                var best = shortlist.Max(c => c.Seeders ?? 0);
                // Real advantage only: at least double the leader, at least
                // three, and not a straggler next to the best of the list.
                if (preferredSeeders >= 3 && preferredSeeders >= leaderSeeders * 2 && preferredSeeders * 2 >= best)
                {
                    claims.Add(ClaimSeeders);
                }
            }

            if (EditionWords.IsMatch(reason) && !target.Abridged)
            {
                var preferredUnabridged = UnabridgedRegex.IsMatch(preferred.Title);
                var leaderAbridged = AbridgedRegex.IsMatch(leader.Title) && !UnabridgedRegex.IsMatch(leader.Title);
                if (preferredUnabridged && leaderAbridged)
                {
                    claims.Add(ClaimEdition);
                }
            }

            return claims;
        }

        private static bool NamesNarrator(CandidateInput candidate, IReadOnlyList<string> narrators)
        {
            var haystack = TitleMatcher.Normalize(candidate.Title + " " + (candidate.Narrator ?? string.Empty));
            if (haystack.Length == 0) return false;
            foreach (var narrator in narrators)
            {
                var normalized = TitleMatcher.Normalize(narrator);
                if (normalized.Length == 0) continue;
                if (haystack.Contains(normalized, StringComparison.Ordinal)) return true;
                // Surname alone is enough when it is distinctive (≥ 5 letters).
                var surname = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (surname is { Length: >= 5 } && Regex.IsMatch(haystack, $@"\b{Regex.Escape(surname)}\b")) return true;
            }
            return false;
        }
    }
}
