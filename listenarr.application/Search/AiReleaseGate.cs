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

                foreach (var verdict in flagged)
                {
                    _logger.LogInformation(
                        "AI release gate flagged candidate for '{Book}': {Release} — {Reason}",
                        LogRedaction.SanitizeText(audiobook.Title),
                        LogRedaction.SanitizeText(shortlist[verdict.Index].SearchResult.Title),
                        LogRedaction.SanitizeText(verdict.Reason));
                }

                var flaggedIndexes = flagged.Select(v => v.Index).ToHashSet();
                var pick = shortlist.Where((_, i) => !flaggedIndexes.Contains(i)).FirstOrDefault();
                // Beyond the shortlist the gate had no opinion; those candidates scored below
                // the shortlist but weren't judged, so they stay eligible rather than being
                // condemned unseen.
                return pick ?? acceptable.Skip(shortlist.Count).FirstOrDefault();
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
    }
}
