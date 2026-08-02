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

namespace Listenarr.Application.Downloads.Submission
{
    // Search-and-download auto-pick path, split out of DownloadService.cs to keep
    // both under the architecture size cap.
    public partial class DownloadService
    {
        public async Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId)
        {
            // Get the audiobook
            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Audiobook not found"
                };
            }

            if (audiobook.QualityProfile == null)
            {
                logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Audiobook has no quality profile assigned"
                };
            }

            // Build search query from audiobook metadata
            var searchQuery = DownloadSearchQueryBuilder.Build(audiobook);
            logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(searchQuery));

            // Search using the working search service. This is an automatic search (triggered
            // by the background/manual 'search-and-download' endpoint), so set isAutomaticSearch
            // to true to ensure only indexers are queried (no Amazon/Audible scraping).
            var searchResults = await searchService.SearchAsync(searchQuery, category: "3030", isAutomaticSearch: true);

            if (searchResults == null || !searchResults.Any())
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No search results found"
                };
            }

            // Context-aware filter pass — reject non-audiobook matter and results whose
            // title isn't relevant to this specific audiobook before scoring picks one
            // to download.
            var preFilterCount = searchResults.Count;
            searchResults = filterPipeline.ApplyFilters(searchResults, logFilteredResults: true, audiobook: audiobook);
            if (searchResults.Count < preFilterCount)
            {
                logger.LogInformation("Filtered {Removed} of {Total} raw results for audiobook '{Title}' via context-aware pipeline",
                    preFilterCount - searchResults.Count, preFilterCount, LogRedaction.SanitizeText(audiobook.Title));
            }
            if (!searchResults.Any())
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No relevant results found (all filtered as non-audiobook or title-irrelevant)"
                };
            }

            // Drop blocklisted releases (wrong-content rejections, reaped stalls,
            // client-reported failures). The sweep path already filters these;
            // without the same filter here, the post-failure re-search re-grabs
            // the exact release that just failed, forever.
            if (blockedReleaseRepository != null)
            {
                var blocked = await blockedReleaseRepository.GetByAudiobookIdAsync(audiobook.Id);
                if (blocked.Count > 0)
                {
                    var beforeBlocked = searchResults.Count;
                    searchResults = searchResults
                        .Where(r => !Search.BlockedReleaseMatcher.IsBlocked(r.Title, r.MagnetLink, blocked))
                        .ToList();
                    if (searchResults.Count < beforeBlocked)
                    {
                        logger.LogInformation("Filtered {Removed} blocked release(s) for audiobook '{Title}'",
                            beforeBlocked - searchResults.Count, LogRedaction.SanitizeText(audiobook.Title));
                    }
                    if (!searchResults.Any())
                    {
                        return new SearchAndDownloadResult
                        {
                            Success = false,
                            Message = "All results are blocklisted releases"
                        };
                    }
                }
            }

            // Score results against quality profile
            // Score results against quality profile (runtime enables the
            // collection-sized-release rejection).
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile, audiobook.Runtime);

            // Log all scored results for debugging
            logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, LogRedaction.SanitizeText(audiobook.Title));
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, LogRedaction.SanitizeText(scoredResult.SearchResult.Title), LogRedaction.SanitizeText(scoredResult.SearchResult.Source),
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);
                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            // Only consider non-rejected, score > 0 results
            var acceptable = scoredResults
                .Where(s => !s.IsRejected && s.TotalScore > 0)
                .OrderByDescending(s => s.TotalScore)
                .ToList();

            if (acceptable.Count == 0)
            {
                logger.LogWarning("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No acceptable search results found"
                };
            }

            // AI release gate: one model call over the shortlist names releases
            // that are clearly not this audiobook (music albums, wrong books);
            // the pick drops to the best unflagged candidate. Fails open — an
            // unreachable endpoint or garbage answer gates nothing, and a
            // wrongly-gated release only waits for the next search cycle.
            var topResult = await PickThroughAiGateAsync(audiobook, acceptable);
            if (topResult == null)
            {
                logger.LogWarning(
                    "AI release gate flagged every acceptable candidate for audiobook '{Title}' — skipping this cycle",
                    LogRedaction.SanitizeText(audiobook.Title));
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "All candidates were flagged as wrong content by the AI release gate"
                };
            }

            // Assign score to SearchResult
            topResult.SearchResult.Score = topResult.TotalScore;

            var candidate = TrustedDownloadCandidateFactory.Create(topResult.SearchResult);
            var isTorrent = candidate.SourceDescriptor.Protocol == DownloadProtocol.Torrent;
            var downloadClientId = await downloadClientSelector.GetAppropriateDownloadClientAsync(isTorrent);

            if (downloadClientId == null)
            {
                logger.LogWarning("No suitable download client found for type: {Type}", isTorrent ? "Torrent" : "NZB");
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = $"No suitable download client found for {(isTorrent ? "torrent" : "NZB")} results"
                };
            }

            // Send to download client with audiobookId for proper metadata linking
            var downloadId2 = await SendToDownloadClientAsync(candidate, downloadClientId, audiobookId);

            // Log to history
            await LogDownloadHistory(audiobook, "Search", topResult.SearchResult);

            return new SearchAndDownloadResult
            {
                Success = true,
                Message = $"Successfully sent to download client",
                DownloadId = downloadId2,
                IndexerUsed = "Search",
                DownloadClientUsed = downloadClientId,
                SearchResult = topResult.SearchResult
            };
        }

        /// <summary>
        /// Returns the best-scored candidate that the AI release gate didn't
        /// flag, or the plain top pick whenever the gate is disabled,
        /// unconfigured, unreachable, or answers garbage. Null only when the
        /// gate confidently flagged the entire shortlist.
        /// </summary>
        private async Task<QualityScore?> PickThroughAiGateAsync(
            Audiobook audiobook,
            List<QualityScore> acceptable)
        {
            try
            {
                var settings = await configurationService.GetApplicationSettingsAsync();
                if (!settings.AiAssistGateSearches || !await aiAssist.IsConfiguredAsync())
                {
                    return acceptable[0];
                }

                var shortlist = acceptable.Take(AiReleaseGateJudge.MaxCandidates).ToList();
                var releases = shortlist
                    .Select((s, i) => new AiReleaseGateJudge.ReleaseInput(i, s.SearchResult.Title ?? string.Empty, s.SearchResult.Size))
                    .ToList();

                var raw = await aiAssist.CompleteJsonAsync(
                    AiReleaseGateJudge.BuildSystemPrompt(),
                    AiReleaseGateJudge.BuildUserPrompt(
                        audiobook.Title ?? string.Empty,
                        audiobook.Authors ?? new List<string>(),
                        audiobook.Runtime,
                        releases));
                if (raw == null) return acceptable[0];

                var validIndexes = releases.Select(r => r.Index).ToHashSet();
                var flagged = AiReleaseGateJudge.ParseResponse(raw, validIndexes);
                if (flagged.Count == 0) return acceptable[0];

                foreach (var verdict in flagged)
                {
                    logger.LogInformation(
                        "AI release gate flagged candidate for '{Book}': {Release} — {Reason}",
                        LogRedaction.SanitizeText(audiobook.Title),
                        LogRedaction.SanitizeText(shortlist[verdict.Index].SearchResult.Title),
                        LogRedaction.SanitizeText(verdict.Reason));
                }

                var flaggedIndexes = flagged.Select(v => v.Index).ToHashSet();
                var pick = shortlist.Where((_, i) => !flaggedIndexes.Contains(i)).FirstOrDefault();
                // Beyond the shortlist the gate had no opinion; those candidates
                // scored below the shortlist but weren't judged, so they remain
                // eligible rather than being condemned unseen.
                return pick ?? acceptable.Skip(shortlist.Count).FirstOrDefault();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "AI release gate failed; using the top-scored candidate");
                return acceptable[0];
            }
        }
    }
}
