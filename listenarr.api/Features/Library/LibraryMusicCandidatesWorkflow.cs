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

using System.Text.Json;
using Listenarr.Application.Audiobooks.Verification;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Read-only sweep for books that "smell like music": runs
    /// <see cref="MusicSmellDetector"/> over flagged/unverifiable books with
    /// files, reusing the verification artifacts already stored on each record
    /// (file durations, transcript, heard credits) — no new transcription.
    /// Feeds the dashboard's review list; the destructive action stays behind
    /// the human-confirmed not-audiobook sweep.
    /// </summary>
    public sealed class LibraryMusicCandidatesWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly ILogger<LibraryMusicCandidatesWorkflow> _logger;

        public LibraryMusicCandidatesWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            ILogger<LibraryMusicCandidatesWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _logger = logger;
        }

        public async Task<IActionResult> GetCandidatesAsync(CancellationToken ct)
        {
            var books = await _repo.GetAllAsync();
            var eligible = books
                .Where(b => b.VerificationStatus is VerificationStatus.AgentFlagged or VerificationStatus.AgentUnverifiable)
                .ToList();

            if (eligible.Count == 0)
            {
                return new OkObjectResult(new { candidates = Array.Empty<object>() });
            }

            var files = await _audioFileRepository.GetAllAsync(ct);
            var filesByBook = files
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var scored = new List<(double Score, object Row)>();
            foreach (var book in eligible)
            {
                ct.ThrowIfCancellationRequested();

                if (!filesByBook.TryGetValue(book.Id, out var bookFiles) || bookFiles.Count == 0)
                {
                    continue; // nothing on disk — nothing to sweep
                }

                var (heardTitle, heardAuthor) = ReadHeardCredits(book.VerificationDetailJson);
                var durations = bookFiles.Select(f => f.DurationSeconds).ToList();
                var result = MusicSmellDetector.Score(
                    book.VerificationStatus,
                    durations,
                    book.VerificationTranscript,
                    heardTitle,
                    heardAuthor,
                    book.Title,
                    book.Authors?.FirstOrDefault());

                if (!result.IsCandidate)
                {
                    continue;
                }

                var known = durations.Where(d => d is > 0).Select(d => d!.Value).OrderBy(d => d).ToList();
                scored.Add((result.Score, new
                {
                    id = book.Id,
                    title = book.Title,
                    score = Math.Round(result.Score, 2),
                    reasons = result.Reasons,
                    fileCount = bookFiles.Count,
                    medianDurationSeconds = known.Count > 0 ? Math.Round(known[known.Count / 2]) : 0
                }));
            }

            _logger.LogInformation(
                "Music-candidate sweep: {Eligible} flagged/unverifiable books scanned, {Candidates} candidate(s) at threshold {Threshold}",
                eligible.Count, scored.Count, MusicSmellDetector.CandidateThreshold);

            return new OkObjectResult(new
            {
                candidates = scored.OrderByDescending(c => c.Score).Select(c => c.Row).ToList()
            });
        }

        /// <summary>
        /// Pulls heardCredits.title/.author out of the stored verdict-detail JSON
        /// (camelCase, string enums). Tolerant by design: malformed or absent
        /// detail simply yields no heard credits.
        /// </summary>
        internal static (string? Title, string? Author) ReadHeardCredits(string? detailJson)
        {
            if (string.IsNullOrWhiteSpace(detailJson)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(detailJson);
                if (!doc.RootElement.TryGetProperty("heardCredits", out var heard) || heard.ValueKind != JsonValueKind.Object)
                {
                    return (null, null);
                }

                string? title = heard.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                string? author = heard.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                return (title, author);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }
}
