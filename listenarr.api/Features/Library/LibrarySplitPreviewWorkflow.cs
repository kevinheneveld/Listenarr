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

using Listenarr.Application.Audiobooks;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Preview for the "Split collection" workflow: cluster a record's files
    /// into per-book groups (subdirectory first, then embedded tag, then
    /// filename stem) and suggest an existing library record for each group.
    /// Read-only — applying is a sequence of file transfers/deletes driven by
    /// the client.
    /// </summary>
    public sealed class LibrarySplitPreviewWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystem _fileSystem;
        private readonly IAiAssistService _aiAssist;
        private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
        private readonly ILogger<LibrarySplitPreviewWorkflow> _logger;

        public LibrarySplitPreviewWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IFfmpegService ffmpegService,
            IFileSystem fileSystem,
            IAiAssistService aiAssist,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
            ILogger<LibrarySplitPreviewWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _ffmpegService = ffmpegService;
            _fileSystem = fileSystem;
            _aiAssist = aiAssist;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IActionResult> PreviewAsync(int id, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var files = await _audioFileRepository.GetByAudiobookIdAsync(id, ct);

            // Read each file's embedded book title (Album/Title tag) so a collection bulk-renamed
            // to the parent record's name still splits by the real book each file belongs to —
            // filenames are useless there, but the tags name the actual book.
            //
            // CRITICAL: parallelize ONLY the raw ffprobe (RunFfprobeAsync spawns a process and
            // touches no DbContext). Higher-level metadata reads hit the request-scoped,
            // non-thread-safe EF DbContext per call; probing those concurrently corrupted
            // results, so the same file read its tag on one run and blank on the next — making
            // the clusters (and therefore the moves) non-deterministic.
            var embeddedTitles = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            try
            {
                // Resolve/install ffprobe once up front so the parallel probes don't race on it.
                await _ffmpegService.GetFfprobePathAsync();

                var targets = files
                    .Select(f => (f.Id, path: FileUtils.CombineWithOptionalBase(audiobook.BasePath, f.Path ?? string.Empty)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.path) && _fileSystem.FileExists(x.path))
                    .ToList();

                await Parallel.ForEachAsync(
                    targets,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (target, token) =>
                    {
                        try
                        {
                            // Cache per file (keyed by path+size — a replaced file
                            // gets a fresh probe): a cold 100+ file collection costs
                            // minutes of ffprobe on slow hosts, which is what pushed
                            // the preview past the reverse proxy's timeout (live
                            // case: a 131-file collection 504'd). Reopening the
                            // modal must not pay that again. Empty result is cached
                            // too — tagless files stay tagless.
                            var cacheKey = $"split_tag_{target.path}_{_fileSystem.GetFileLength(target.path)}";
                            if (!_cache.TryGetValue(cacheKey, out string? bookTitle))
                            {
                                var meta = await _ffmpegService.RunFfprobeAsync(target.path);
                                bookTitle = !string.IsNullOrWhiteSpace(meta?.Album) ? meta!.Album : meta?.Title;
                                // Per-chapter Title tags ("Ch75 - The Hard Way")
                                // must cluster as the BOOK, not as 77 one-file
                                // "books" (live case: a chapterized rip offered 77
                                // groups, one per chapter). Strip the chapter
                                // marker; a tag that is ONLY a chapter marker
                                // ("Chapter 12") carries no book identity at all.
                                bookTitle = EmbeddedTitleNormalizer.StripChapterMarkers(bookTitle);
                                _cache.Set(cacheKey, bookTitle ?? string.Empty, TimeSpan.FromHours(6));
                            }
                            if (!string.IsNullOrWhiteSpace(bookTitle))
                            {
                                embeddedTitles[target.Id] = bookTitle!;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "ffprobe failed for split clustering (file {FileId})", target.Id);
                        }
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Tag reads are an enhancement to clustering, not a requirement —
                // fall back to path/stem clustering when ffprobe is unavailable.
                _logger.LogDebug(ex, "Embedded-tag read skipped for split preview of audiobook {AudiobookId}", id);
            }

            var clusters = FileClustering.Cluster(files, audiobook.BasePath, embeddedTitles);

            // Suggestion candidates: same-author records first (a collection
            // dump is almost always one author's shelf), then the whole library
            // as fallback. The suggester prefers the longest matching title, so
            // ordering only matters for the fallback breadth.
            var all = await _repo.GetAllAsync();
            var sourceAuthors = new HashSet<string>(
                audiobook.Authors ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var sameAuthor = new List<(int, string)>();
            var others = new List<(int, string)>();
            foreach (var candidate in all)
            {
                if (candidate.Id == id || string.IsNullOrWhiteSpace(candidate.Title)) continue;
                var isSameAuthor = (candidate.Authors ?? new List<string>()).Any(sourceAuthors.Contains);
                (isSameAuthor ? sameAuthor : others).Add((candidate.Id, candidate.Title!));
            }
            var titleById = all.Where(a => a.Id != id).ToDictionary(a => a.Id, a => a.Title ?? string.Empty);
            var subtitleById = all.Where(a => a.Id != id).ToDictionary(a => a.Id, a => a.Subtitle ?? string.Empty);

            var deterministic = clusters.Select(cluster => new
            {
                cluster.Key,
                cluster.DisplayName,
                cluster.Files,
                Suggested = SplitDestinationSuggester.Suggest(cluster.DisplayName, sameAuthor)
                            ?? SplitDestinationSuggester.Suggest(cluster.DisplayName, others)
            }).ToList();

            // Optional AI pass: a language model reviews every group against
            // the candidate list and can fill gaps the substring matcher
            // missed ("Rama" → "Rendezvous with Rama") or fix its junk
            // matches ("Space Trilogy" → "Space"). Advisory only — the user
            // confirms every group — and any failure keeps the deterministic
            // suggestions untouched.
            var aiSuggestions = await TryRefineWithAiAsync(audiobook, deterministic
                .Select(d => new SplitSuggestionAiRefiner.ClusterInput(
                    d.Key,
                    d.DisplayName,
                    d.Files.Take(SplitSuggestionAiRefiner.MaxSampleFileNames)
                        .Select(f => Path.GetFileName(f.Path ?? string.Empty)).ToList(),
                    d.Suggested))
                .ToList(), sameAuthor, others, ct);

            var response = deterministic.Select(d =>
            {
                var fromAi = aiSuggestions != null && aiSuggestions.TryGetValue(d.Key, out var aiTarget);
                var suggested = fromAi && aiSuggestions != null ? aiSuggestions[d.Key] : d.Suggested;

                // Digit guard: a suggestion whose number contradicts the
                // cluster's is always wrong (live case: the AI pass proposed
                // "… Volume 4" for a "… Volume 1" cluster, and containment
                // matched a plain-titled sibling whose subtitle said
                // "Volume 3"). No suggestion beats a wrong one — the picker
                // is digit-aware now.
                bool Conflicts(int? sid) => sid.HasValue
                    && SplitDestinationSuggester.DigitsConflict(
                        d.DisplayName,
                        titleById.GetValueOrDefault(sid.Value),
                        subtitleById.GetValueOrDefault(sid.Value));
                if (Conflicts(suggested))
                {
                    suggested = fromAi && !Conflicts(d.Suggested) ? d.Suggested : null;
                    fromAi = false;
                }

                return new
                {
                    key = d.Key,
                    displayName = d.DisplayName,
                    fileIds = d.Files.Select(f => f.Id).ToList(),
                    fileNames = d.Files
                        .Select(f => Path.GetFileName(f.Path ?? string.Empty))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    suggestedTargetId = suggested,
                    suggestedTargetTitle = suggested.HasValue && titleById.TryGetValue(suggested.Value, out var t) ? t : null,
                    suggestionSource = suggested == null ? null : (fromAi ? "ai" : "title-match")
                };
            }).ToList();

            return new OkObjectResult(new { audiobookId = id, clusters = response });
        }

        private async Task<Dictionary<string, int?>?> TryRefineWithAiAsync(
            Audiobook audiobook,
            IReadOnlyList<SplitSuggestionAiRefiner.ClusterInput> clusterInputs,
            List<(int Id, string Title)> sameAuthor,
            List<(int Id, string Title)> others,
            CancellationToken ct)
        {
            try
            {
                if (!await _aiAssist.IsConfiguredAsync(ct)) return null;

                // Same-author candidates first — they almost always contain the
                // answer — then pad with the rest of the library up to the cap.
                var candidates = sameAuthor
                    .Concat(others)
                    .Take(SplitSuggestionAiRefiner.MaxCandidates)
                    .Select(c => new SplitSuggestionAiRefiner.CandidateInput(c.Id, c.Title))
                    .ToList();
                if (candidates.Count == 0 || clusterInputs.Count == 0) return null;

                var raw = await _aiAssist.CompleteJsonAsync(
                    SplitSuggestionAiRefiner.BuildSystemPrompt(),
                    SplitSuggestionAiRefiner.BuildUserPrompt(
                        audiobook.Title ?? string.Empty,
                        audiobook.Authors ?? new List<string>(),
                        clusterInputs,
                        candidates),
                    ct);
                if (raw == null) return null;

                var validIds = candidates.Select(c => c.Id).ToHashSet();
                var parsed = SplitSuggestionAiRefiner.ParseResponse(raw, validIds);
                if (parsed.Count > 0)
                {
                    _logger.LogInformation(
                        "AI assist refined split suggestions for audiobook {Id}: {Count} of {Total} groups answered",
                        audiobook.Id, parsed.Count, clusterInputs.Count);
                }
                return parsed.Count > 0 ? parsed : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "AI assist split refinement failed; keeping deterministic suggestions");
                return null;
            }
        }
    }
}
