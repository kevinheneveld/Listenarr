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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Read-only sweep for the two flavors of duplication a library accretes:
    /// duplicate RECORDS (the same book tracked twice — same ASIN, same
    /// normalized title+author when neither carries a conflicting ASIN, or
    /// two records whose tracked files are byte-for-byte the same set) and
    /// duplicate COPIES (one record holding the same audio twice under two
    /// filename schemes, detected via <see cref="FileClustering"/> signatures).
    /// Conservative by design: same title with different subtitles AND
    /// different years, or two distinct ASINs, are treated as different
    /// editions, not duplicates — false positives erode trust in the list.
    /// The identical-files pass is the exception to the ASIN rule: a
    /// catalog can list one narration under several ASINs (regional
    /// re-releases, series-bundle listings, relabels), and a library that
    /// copied the same rip into two records is a duplicate no matter what
    /// the metadata says. It groups strictly on the size multiset of every
    /// tracked file, which at whole-book scale is not a coincidence.
    /// </summary>
    public sealed class LibraryDuplicatesWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly ILogger<LibraryDuplicatesWorkflow> _logger;

        public LibraryDuplicatesWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            ILogger<LibraryDuplicatesWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _logger = logger;
        }

        public async Task<IActionResult> GetDuplicatesAsync(CancellationToken ct)
        {
            // Deliberately NOT cached: this runs behind an explicit "Scan"
            // click, and a re-scan must reflect books added since the last one.
            return new OkObjectResult(await ComputeDuplicatesPayloadAsync(ct));
        }

        /// <summary>
        /// Every tracked file's size must be known, and the set must be at
        /// least this large before byte-identity across records counts as
        /// evidence. Whole-book rips are hundreds of MB; a 5 MB stub matching
        /// another stub proves nothing.
        /// </summary>
        internal const long IdenticalFilesMinTotalBytes = 50L * 1024 * 1024;

        /// <summary>
        /// The grouping key for pass (c): file count plus the ascending list
        /// of sizes, or null when the record has no files, any size is
        /// unknown, or the set is below <see cref="IdenticalFilesMinTotalBytes"/>.
        /// </summary>
        internal static string? IdenticalFilesSignature(
            Audiobook book,
            IReadOnlyDictionary<int, List<AudiobookFile>> filesByBook)
        {
            if (!filesByBook.TryGetValue(book.Id, out var files) || files.Count == 0) return null;
            var sizes = new List<long>(files.Count);
            long total = 0;
            foreach (var f in files)
            {
                if (f.Size is not > 0) return null;
                sizes.Add(f.Size.Value);
                total += f.Size.Value;
            }
            if (total < IdenticalFilesMinTotalBytes) return null;
            sizes.Sort();
            return $"{sizes.Count}:{string.Join(',', sizes)}";
        }

        private async Task<object> ComputeDuplicatesPayloadAsync(CancellationToken ct)
        {
            var books = await _repo.GetAllAsync();
            var files = await _audioFileRepository.GetAllAsync(ct);
            var filesByBook = files
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            object Summarize(Audiobook b)
            {
                filesByBook.TryGetValue(b.Id, out var bf);
                return new
                {
                    id = b.Id,
                    title = b.Title,
                    subtitle = b.Subtitle,
                    publishYear = b.PublishYear,
                    asin = b.Asin,
                    fileCount = bf?.Count ?? 0,
                    fileSize = bf?.Sum(f => f.Size ?? 0) ?? 0,
                    monitored = b.Monitored
                };
            }

            int SuggestKeeper(List<Audiobook> group) => group
                .OrderByDescending(b => filesByBook.TryGetValue(b.Id, out var bf) ? bf.Count : 0)
                .ThenBy(b => b.Id)
                .First().Id;

            var duplicateGroups = new List<object>();
            var groupedIds = new HashSet<int>();

            // Pass (a): identical non-empty ASIN — the strongest duplicate signal.
            foreach (var g in books
                .Where(b => !string.IsNullOrWhiteSpace(b.Asin))
                .GroupBy(b => b.Asin!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1))
            {
                var members = g.ToList();
                foreach (var b in members) groupedIds.Add(b.Id);
                duplicateGroups.Add(new
                {
                    key = $"asin:{g.Key}",
                    reason = "asin",
                    books = members.Select(Summarize).ToList(),
                    suggestedKeeperId = SuggestKeeper(members)
                });
            }

            // Pass (b): normalized title + primary author for everything not
            // already grouped by ASIN.
            foreach (var g in books
                .Where(b => !groupedIds.Contains(b.Id) && !string.IsNullOrWhiteSpace(b.Title))
                .GroupBy(b =>
                    TitleMatcher.Normalize(b.Title) + "|" + TitleMatcher.Normalize(b.Authors?.FirstOrDefault()))
                .Where(g => g.Count() > 1))
            {
                var members = g.ToList();

                // Two distinct non-empty ASINs = two catalog editions, not a dupe.
                var distinctAsins = members
                    .Select(b => b.Asin?.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (distinctAsins >= 2) continue;

                // Different subtitles AND different years = probably different
                // editions/volumes sharing a series-ish title. Leave them alone.
                var distinctSubtitles = members
                    .Select(b => TitleMatcher.Normalize(b.Subtitle))
                    .Where(s => s.Length > 0)
                    .Distinct()
                    .Count();
                var distinctYears = members
                    .Select(b => b.PublishYear?.Trim())
                    .Where(y => !string.IsNullOrWhiteSpace(y))
                    .Distinct()
                    .Count();
                if (distinctSubtitles >= 2 && distinctYears >= 2) continue;

                duplicateGroups.Add(new
                {
                    key = $"title-author:{g.Key}",
                    reason = "title-author",
                    books = members.Select(Summarize).ToList(),
                    suggestedKeeperId = SuggestKeeper(members)
                });
            }

            // Pass (c): identical tracked files. Two records whose file rows
            // carry the same multiset of sizes hold the same audio, whatever
            // their titles/ASINs say — the copy got there by a transfer, a
            // relabel, or an organize move that copied instead of moving.
            // The ASIN-strict passes above cannot see these (two distinct
            // ASINs = "different editions"), which is exactly how a library
            // ends up with four records over one set of files. Sizes must be
            // known for every file and the set must be book-sized: a lone
            // small file matching another to the byte is a dice roll.
            foreach (var g in books
                .Where(b => !groupedIds.Contains(b.Id))
                .Select(b => (book: b, signature: IdenticalFilesSignature(b, filesByBook)))
                .Where(x => x.signature != null)
                .GroupBy(x => x.signature!, StringComparer.Ordinal)
                .Where(g => g.Count() > 1))
            {
                var members = g.Select(x => x.book).ToList();
                foreach (var b in members) groupedIds.Add(b.Id);
                duplicateGroups.Add(new
                {
                    key = $"identical-files:{g.Key}",
                    reason = "identical-files",
                    books = members.Select(Summarize).ToList(),
                    suggestedKeeperId = SuggestKeeper(members)
                });
            }

            // Duplicate COPIES: one record, the same book's audio twice. Flat
            // files only (stem clusters) — disc/part subfolders of a single copy
            // ("CD1"/"CD2") are legitimate multi-cluster layouts, so dir clusters
            // never count toward this signal.
            var duplicateCopyBooks = new List<object>();
            foreach (var b in books)
            {
                ct.ThrowIfCancellationRequested();
                if (!filesByBook.TryGetValue(b.Id, out var bf) || bf.Count < 2) continue;

                var clusters = FileClustering.Cluster(bf, b.BasePath);
                if (clusters.Count < 2) continue;

                var stemClusters = clusters
                    .Select(c => (cluster: c, key: c.Key))
                    .Where(x => x.key.StartsWith("stem:", StringComparison.Ordinal))
                    .Select(x =>
                    {
                        // Key shape: "stem:<lower-stem>|<signature>" — signatures
                        // never contain '|', so the last one is the separator.
                        var sep = x.key.LastIndexOf('|');
                        return (x.cluster, stem: x.key[5..sep]);
                    })
                    .ToList();

                // (i) Same stem rendered with two numbering styles — the
                // signature-identity case the Split workflow can't separate.
                var hit = stemClusters
                    .GroupBy(x => x.stem, StringComparer.OrdinalIgnoreCase)
                    .Any(g => g.Count() > 1);

                // (ii) Two multi-file flat clusters covering near-identical total
                // runtime — same book under two unrelated filename schemes.
                if (!hit)
                {
                    var totals = stemClusters
                        .Where(x => x.cluster.Files.Count > 1)
                        .Select(x => x.cluster.Files.Sum(f => f.DurationSeconds ?? 0))
                        .Where(t => t > 0)
                        .OrderBy(t => t)
                        .ToList();
                    for (var i = 1; i < totals.Count && !hit; i++)
                    {
                        if (totals[i - 1] / totals[i] >= 0.75) hit = true;
                    }
                }

                if (hit)
                {
                    duplicateCopyBooks.Add(new
                    {
                        id = b.Id,
                        title = b.Title,
                        clusterCount = clusters.Count,
                        fileCount = bf.Count
                    });
                }
            }

            _logger.LogInformation(
                "Duplicate sweep: {Groups} duplicate-record group(s), {Copies} record(s) with duplicate copies across {Books} books",
                duplicateGroups.Count, duplicateCopyBooks.Count, books.Count);

            return new { duplicateGroups, duplicateCopyBooks };
        }
    }
}
