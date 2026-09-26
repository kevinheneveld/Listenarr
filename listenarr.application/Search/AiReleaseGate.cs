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

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// The AI release gate: one model call over the scored shortlist names releases that are
    /// clearly not this audiobook (music albums, a different book sharing a word of the title),
    /// and the pick drops to the best unflagged candidate. Fails open — disabled, unconfigured,
    /// unreachable, or a garbage answer gates nothing, and a wrongly-gated release only waits for
    /// the next search cycle.
    ///
    /// Shared by the per-book search-and-download path and the automatic-search sweep. Live case
    /// for the sweep: it never consulted the gate and grabbed "White Rural Rage: The Threat to
    /// American Democracy" for James Patterson's "American Rage" off a title-only fallback query.
    /// </summary>
    public sealed class AiReleaseGate
    {
        private readonly IAiAssistService _aiAssist;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger _logger;

        public AiReleaseGate(IAiAssistService aiAssist, IConfigurationService configurationService, ILogger logger)
        {
            _aiAssist = aiAssist;
            _configurationService = configurationService;
            _logger = logger;
        }

        /// <summary>
        /// Returns the best-scored candidate the gate didn't flag, the plain top pick whenever the
        /// gate is off or fails, and null only when it confidently flagged the entire shortlist
        /// (and nothing scored below it).
        /// </summary>
        public async Task<QualityScore?> PickAsync(Audiobook audiobook, IReadOnlyList<QualityScore> acceptable, CancellationToken ct = default)
        {
            if (acceptable.Count == 0)
            {
                return null;
            }

            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                if (!settings.AiAssistGateSearches || !await _aiAssist.IsConfiguredAsync(ct))
                {
                    return acceptable[0];
                }

                var shortlist = acceptable.Take(AiReleaseGateJudge.MaxCandidates).ToList();
                return settings.AiAssistRankSearches
                    ? await RankAsync(audiobook, acceptable, shortlist, ct)
                    : await GateAsync(audiobook, acceptable, shortlist, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "AI release gate failed; using the top-scored candidate");
                return acceptable[0];
            }
        }

        /// <summary>Screen only: best unflagged candidate in score order.</summary>
        private async Task<QualityScore?> GateAsync(
            Audiobook audiobook,
            IReadOnlyList<QualityScore> acceptable,
            List<QualityScore> shortlist,
            CancellationToken ct)
        {
            var releases = shortlist
                .Select((s, i) => new AiReleaseGateJudge.ReleaseInput(i, s.SearchResult.Title ?? string.Empty, s.SearchResult.Size))
                .ToList();

            var raw = await _aiAssist.CompleteJsonAsync(
                AiReleaseGateJudge.BuildSystemPrompt(),
                AiReleaseGateJudge.BuildUserPrompt(
                    audiobook.Title ?? string.Empty,
                    audiobook.Authors ?? new List<string>(),
                    audiobook.Runtime,
                    releases),
                ct);
            if (raw == null) return acceptable[0];

            var validIndexes = releases.Select(r => r.Index).ToHashSet();
            var flagged = AiReleaseGateJudge.ParseResponse(raw, validIndexes);
            if (flagged.Count == 0) return acceptable[0];

            LogFlagged(audiobook, shortlist, flagged);
            return PickUnflagged(acceptable, shortlist, flagged.Select(v => v.Index).ToHashSet());
        }

        /// <summary>
        /// Screen and rank: the model orders the unflagged shortlist with a
        /// reason each; a preference over the score leader is honoured only
        /// when the reason is a fact the metadata confirms (see
        /// <see cref="AiReleaseRankJudge.ValidateClaims"/>). Every ranking is
        /// logged so the operator can audit the model before trusting it.
        /// </summary>
        private async Task<QualityScore?> RankAsync(
            Audiobook audiobook,
            IReadOnlyList<QualityScore> acceptable,
            List<QualityScore> shortlist,
            CancellationToken ct)
        {
            var target = new AiReleaseRankJudge.TargetInput(
                audiobook.Title ?? string.Empty,
                audiobook.Authors ?? new List<string>(),
                (audiobook.Narrators ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                audiobook.Runtime,
                audiobook.Abridged == true);
            var candidates = shortlist.Select((s, i) => ToCandidate(i, s.SearchResult)).ToList();

            var raw = await _aiAssist.CompleteJsonAsync(
                AiReleaseRankJudge.BuildSystemPrompt(),
                AiReleaseRankJudge.BuildUserPrompt(target, candidates),
                ct);
            if (raw == null) return acceptable[0];

            var validIndexes = candidates.Select(c => c.Index).ToHashSet();
            var response = AiReleaseRankJudge.ParseResponse(raw, validIndexes);
            if (response.Rejects.Count > 0) LogFlagged(audiobook, shortlist, response.Rejects);

            var flaggedIndexes = response.Rejects.Select(v => v.Index).ToHashSet();
            var leader = PickUnflagged(acceptable, shortlist, flaggedIndexes);
            if (leader == null) return null;
            var leaderIndex = shortlist.IndexOf(leader);
            if (leaderIndex < 0 || response.Ranking.Count == 0) return leader;

            _logger.LogInformation(
                "AI release ranker for '{Book}': {Ranking}",
                LogRedaction.SanitizeText(audiobook.Title),
                string.Join(" | ", response.Ranking.Select(r =>
                    $"#{r.Index} {LogRedaction.SanitizeText(candidates[r.Index].Title)} — {LogRedaction.SanitizeText(r.Reason)}")));

            foreach (var ranked in response.Ranking)
            {
                if (flaggedIndexes.Contains(ranked.Index)) continue;
                if (ranked.Index == leaderIndex)
                {
                    return leader;
                }

                var claims = AiReleaseRankJudge.ValidateClaims(
                    ranked.Reason, candidates[ranked.Index], candidates[leaderIndex], candidates, target);
                if (claims.Count > 0)
                {
                    _logger.LogInformation(
                        "AI release ranker preferred '{Pick}' over score leader '{Leader}' for '{Book}' ({Claims}): {Reason}",
                        LogRedaction.SanitizeText(candidates[ranked.Index].Title),
                        LogRedaction.SanitizeText(candidates[leaderIndex].Title),
                        LogRedaction.SanitizeText(audiobook.Title),
                        string.Join(", ", claims),
                        LogRedaction.SanitizeText(ranked.Reason));
                    return shortlist[ranked.Index];
                }

                _logger.LogInformation(
                    "AI release ranker preference '{Pick}' for '{Book}' not validated by metadata ({Reason}) — keeping score order",
                    LogRedaction.SanitizeText(candidates[ranked.Index].Title),
                    LogRedaction.SanitizeText(audiobook.Title),
                    LogRedaction.SanitizeText(ranked.Reason));
            }

            return leader;
        }

        private static AiReleaseRankJudge.CandidateInput ToCandidate(int index, SearchResult result)
        {
            int? ageDays = null;
            if (DateTime.TryParse(result.PublishedDate, out var published))
            {
                var age = (int)Math.Floor((DateTime.UtcNow - published.ToUniversalTime()).TotalDays);
                ageDays = Math.Max(0, age);
            }

            return new AiReleaseRankJudge.CandidateInput(
                index,
                result.Title ?? string.Empty,
                result.Size > 0 ? result.Size : null,
                result.Seeders,
                result.Leechers,
                string.IsNullOrWhiteSpace(result.Format) ? null : result.Format,
                string.IsNullOrWhiteSpace(result.DownloadType) ? null : result.DownloadType,
                string.IsNullOrWhiteSpace(result.Source) ? null : result.Source,
                ageDays,
                string.IsNullOrWhiteSpace(result.Narrator) ? null : result.Narrator);
        }

        private void LogFlagged(Audiobook audiobook, List<QualityScore> shortlist, IReadOnlyList<AiReleaseGateJudge.GateVerdict> flagged)
        {
            foreach (var verdict in flagged)
            {
                _logger.LogInformation(
                    "AI release gate flagged candidate for '{Book}': {Release} — {Reason}",
                    LogRedaction.SanitizeText(audiobook.Title),
                    LogRedaction.SanitizeText(shortlist[verdict.Index].SearchResult.Title),
                    LogRedaction.SanitizeText(verdict.Reason));
            }
        }

        /// <summary>
        /// Best unflagged candidate in score order. Beyond the shortlist the
        /// model had no opinion; those candidates scored below the shortlist
        /// but were not judged, so they stay eligible rather than being
        /// condemned unseen.
        /// </summary>
        private static QualityScore? PickUnflagged(IReadOnlyList<QualityScore> acceptable, List<QualityScore> shortlist, HashSet<int> flaggedIndexes)
        {
            var pick = shortlist.Where((_, i) => !flaggedIndexes.Contains(i)).FirstOrDefault();
            return pick ?? acceptable.Skip(shortlist.Count).FirstOrDefault();
        }
    }
}
