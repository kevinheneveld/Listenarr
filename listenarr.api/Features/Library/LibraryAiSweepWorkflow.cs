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
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// AI library sweep: reviews the vetting backlog (records with files whose
    /// verification isn't settled) by file names, flagging obvious wrong
    /// content — music albums, different books, collections under one title.
    /// Cursor-paged and capped per invocation because each batch is a slow
    /// LLM round-trip; the client re-invokes with lastId to continue.
    /// Flag-only: results are returned for human review, nothing is changed.
    /// </summary>
    public sealed class LibraryAiSweepWorkflow
    {
        private const int DefaultLimit = 25;
        private const int MaxLimit = 100;

        private readonly IAudiobookRepository _repo;
        private readonly IAiAssistService _aiAssist;
        private readonly ILogger<LibraryAiSweepWorkflow> _logger;

        public LibraryAiSweepWorkflow(
            IAudiobookRepository repo,
            IAiAssistService aiAssist,
            ILogger<LibraryAiSweepWorkflow> logger)
        {
            _repo = repo;
            _aiAssist = aiAssist;
            _logger = logger;
        }

        public async Task<IActionResult> SweepAsync(int limit, int afterId, CancellationToken ct)
        {
            if (!await _aiAssist.IsConfiguredAsync(ct))
            {
                return new BadRequestObjectResult(new
                {
                    message = "AI assist is not configured — enable it under Settings → AI Assist first."
                });
            }

            limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

            // The vetting backlog: records with files whose verification isn't
            // settled. Manually verified/rejected records already had human
            // eyes; agent-verified ones had whisper actually listen.
            var all = await _repo.GetAllAsync();
            var backlogIds = all
                .Where(a => a.Id > afterId
                    && a.VerificationStatus is VerificationStatus.Unverified
                        or VerificationStatus.AgentFlagged
                        or VerificationStatus.AgentUnverifiable)
                .OrderBy(a => a.Id)
                .Select(a => a.Id)
                .ToList();

            var withFiles = (await _repo.GetByIdsWithFilesAsync(backlogIds.Take(limit * 2), ct))
                .Where(a => a.Files is { Count: > 0 })
                .OrderBy(a => a.Id)
                .Take(limit)
                .ToList();

            if (withFiles.Count == 0)
            {
                return new OkObjectResult(new
                {
                    checkedCount = 0,
                    lastId = (int?)null,
                    exhausted = true,
                    suspicious = Array.Empty<object>()
                });
            }

            var titleById = withFiles.ToDictionary(a => a.Id, a => a.Title ?? $"id {a.Id}");
            var suspicious = new List<object>();
            var checkedCount = 0;

            foreach (var batch in withFiles.Chunk(AiLibrarySweepJudge.RecordsPerCall))
            {
                ct.ThrowIfCancellationRequested();

                var inputs = batch.Select(a => new AiLibrarySweepJudge.RecordInput(
                    a.Id,
                    a.Title ?? string.Empty,
                    a.Authors ?? new List<string>(),
                    a.Files!.Count,
                    SampleFileNames(a))).ToList();

                var raw = await _aiAssist.CompleteJsonAsync(
                    AiLibrarySweepJudge.BuildSystemPrompt(),
                    AiLibrarySweepJudge.BuildUserPrompt(inputs),
                    ct);
                checkedCount += batch.Length;
                if (raw == null) continue; // endpoint hiccup — batch counts as checked-without-opinion

                var validIds = batch.Select(a => a.Id).ToHashSet();
                foreach (var verdict in AiLibrarySweepJudge.ParseResponse(raw, validIds))
                {
                    suspicious.Add(new
                    {
                        audiobookId = verdict.Id,
                        title = titleById[verdict.Id],
                        reason = verdict.Reason
                    });
                }
            }

            var lastId = withFiles[^1].Id;
            var exhausted = backlogIds.Count <= limit * 2 && withFiles.Count < limit;

            _logger.LogInformation(
                "AI library sweep checked {Checked} record(s) after id {AfterId}: {Suspicious} flagged",
                checkedCount, afterId, suspicious.Count);

            return new OkObjectResult(new
            {
                checkedCount,
                lastId = (int?)lastId,
                exhausted,
                suspicious
            });
        }

        /// <summary>
        /// A representative slice of the record's file names: firsts and lasts
        /// in natural order, so both "Track 01" openers and stray tail files
        /// (a bonus music album dumped after the book) surface in the sample.
        /// </summary>
        private static List<string> SampleFileNames(Audiobook audiobook)
        {
            var ordered = AudiobookFileOrdering.InNaturalOrder(audiobook.Files)
                .Select(f => Path.GetFileName(f.Path ?? string.Empty))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            if (ordered.Count <= AiLibrarySweepJudge.MaxFileNamesPerRecord)
            {
                return ordered;
            }

            var head = AiLibrarySweepJudge.MaxFileNamesPerRecord / 2;
            var tail = AiLibrarySweepJudge.MaxFileNamesPerRecord - head;
            return ordered.Take(head).Concat(ordered.TakeLast(tail)).ToList();
        }
    }
}
