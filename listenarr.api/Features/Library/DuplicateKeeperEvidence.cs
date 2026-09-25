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
using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Application.Search;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Which record in a duplicate group carries the label the audio actually
    /// announces. The verification engine already transcribed the opening
    /// credits ("… read for you by Dick Hill"); every record's narrator field
    /// is checked against that credit, so a group of byte-identical files
    /// labelled "Dick Hill" and "Jeff Harding" resolves without a model call.
    /// Deterministic and advisory: it recommends a keeper, it never merges.
    /// </summary>
    public static class DuplicateKeeperEvidence
    {
        /// <summary>Exactly one record lists the credited narrator.</summary>
        public const string VerdictClear = "clear";
        /// <summary>Several records list the credited narrator — the labels agree; choose by title/edition.</summary>
        public const string VerdictAgree = "agree";
        /// <summary>The credits name a narrator no record lists — every label here looks wrong.</summary>
        public const string VerdictNoneFit = "none-fit";
        /// <summary>No transcript, or a transcript with no narrator credit.</summary>
        public const string VerdictNoEvidence = "no-evidence";

        public sealed record BookEvidence(
            int Id,
            IReadOnlyList<string> Narrators,
            string? HeardNarrator,
            bool? NarratorFits,
            string? HeardTitle,
            bool? TitleFits,
            string? VerifiedBy,
            double? VerificationConfidence,
            bool HasTranscript);

        public sealed record GroupEvidence(
            string Verdict,
            int? RecommendedKeeperId,
            string? HeardNarrator,
            string? HeardTitle,
            string Summary,
            IReadOnlyList<BookEvidence> Books);

        /// <param name="books">The group's records.</param>
        /// <param name="sharedAudio">
        /// True when the records track the same bytes (identical-files groups):
        /// one record's transcript then speaks for every record, so a never-
        /// verified twin is still judged against the credits its sibling heard.
        /// </param>
        public static GroupEvidence Evaluate(IReadOnlyList<Audiobook> books, bool sharedAudio)
        {
            var heard = books
                .Select(b => (book: b, credits: HeardCreditsOf(b)))
                .ToList();

            // The group's reference credit: for shared audio, the most confident
            // transcript that names a narrator; otherwise each record only
            // answers for its own recording.
            var reference = sharedAudio
                ? heard
                    .Where(h => h.credits.Narrator != null)
                    .OrderByDescending(h => h.book.VerificationConfidence ?? 0)
                    .Select(h => h.credits)
                    .FirstOrDefault()
                : null;

            var evidence = new List<BookEvidence>(books.Count);
            foreach (var (book, own) in heard)
            {
                var credits = sharedAudio ? reference ?? own : own;
                var narrators = (book.Narrators ?? [])
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                var narratorFits = credits.Narrator == null || narrators.Count == 0
                    ? (bool?)null
                    : narrators.Any(n => NamesAgree(n, credits.Narrator));
                var titleFits = credits.Title == null || string.IsNullOrWhiteSpace(book.Title)
                    ? (bool?)null
                    : TitleAgrees(book, credits.Title);
                evidence.Add(new BookEvidence(
                    book.Id,
                    narrators,
                    credits.Narrator,
                    narratorFits,
                    credits.Title,
                    titleFits,
                    book.VerifiedBy,
                    book.VerificationConfidence,
                    !string.IsNullOrWhiteSpace(book.VerificationTranscript)));
            }

            var heardNarrator = reference?.Narrator ?? evidence.Select(e => e.HeardNarrator).FirstOrDefault(n => n != null);
            var heardTitle = reference?.Title ?? evidence.Select(e => e.HeardTitle).FirstOrDefault(t => t != null);

            if (heardNarrator == null)
            {
                var transcripts = evidence.Count(e => e.HasTranscript);
                return new GroupEvidence(
                    VerdictNoEvidence,
                    null,
                    null,
                    heardTitle,
                    transcripts == 0
                        ? "No transcript on any of these records — run verification on one of them and rescan."
                        : "The transcript carries no narrator credit, so the audio cannot tell these labels apart.",
                    evidence);
            }

            var fits = evidence.Where(e => e.NarratorFits == true).ToList();
            if (fits.Count == 1)
            {
                var keeper = books.First(b => b.Id == fits[0].Id);
                return new GroupEvidence(
                    VerdictClear,
                    keeper.Id,
                    heardNarrator,
                    heardTitle,
                    $"The audio credits {heardNarrator}; only \"{keeper.Title}\" (id {keeper.Id}) lists that narrator.",
                    evidence);
            }

            if (fits.Count > 1)
            {
                return new GroupEvidence(
                    VerdictAgree,
                    null,
                    heardNarrator,
                    heardTitle,
                    $"The audio credits {heardNarrator}; {fits.Count} of these records list that narrator — the labels agree, choose by title or edition.",
                    evidence);
            }

            var unknown = evidence.Count(e => e.NarratorFits == null);
            return new GroupEvidence(
                VerdictNoneFit,
                null,
                heardNarrator,
                heardTitle,
                unknown == evidence.Count
                    ? $"The audio credits {heardNarrator}, but none of these records names a narrator to compare."
                    : $"The audio credits {heardNarrator}; none of these records lists that narrator — every label here looks wrong.",
                evidence);
        }

        /// <summary>
        /// Credits as heard: the opening transcript re-parsed (the extractor
        /// keeps improving; stored heardCredits may predate a fix), then the
        /// stored verdict's heard credits, then the verifier's own narrator
        /// hit — a strong match against the record's narrator is itself a
        /// heard credit, even when the phrase parser missed it.
        /// </summary>
        private static SpokenCredits HeardCreditsOf(Audiobook book)
        {
            var verdict = VerificationDetailSerializer.TryDeserialize(book.VerificationDetailJson);
            var parsed = SpokenCreditsExtractor.Extract(book.VerificationTranscript);
            var stored = verdict?.HeardCredits;
            var matched = verdict?.NarratorMatch is { Score: >= 0.8, MatchedText: { Length: > 0 } text }
                ? text
                : null;
            return new SpokenCredits
            {
                Narrator = parsed?.Narrator ?? stored?.Narrator ?? matched,
                Title = parsed?.Title ?? stored?.Title,
                Author = parsed?.Author ?? stored?.Author,
                Publisher = parsed?.Publisher ?? stored?.Publisher,
            };
        }

        /// <summary>
        /// Speech-to-text mangles names phonetically ("Eduardo Valarini" for
        /// Edoardo Ballerini, "Aunt Blasnick" for Anne Flosnik, "Jennifer
        /// Eketa" for Ikeda), so tokens are compared on a consonant skeleton
        /// that folds the sounds whisper confuses (b/v/p/f, d/t, k/c/g/q/j,
        /// s/z, m/n, l/r) besides exact and edit-distance agreement. Short
        /// tokens must match exactly — "Hill" and "Hall" share a skeleton but
        /// are different people. A record's narrator agrees with the credit
        /// when at least half of its tokens agree and, for multi-word names,
        /// the surname does.
        /// </summary>
        internal static bool NamesAgree(string recordNarrator, string heardNarrator)
        {
            var a = TitleMatcher.Normalize(recordNarrator);
            var b = TitleMatcher.Normalize(heardNarrator);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return true;

            var recordTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).ToList();
            if (recordTokens.Count == 0) return false;
            var heardTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).ToList();
            if (heardTokens.Count == 0) return false;

            var agreeing = recordTokens.Count(rt => heardTokens.Any(ht => TokensAgree(rt, ht)));
            if (agreeing * 2 < recordTokens.Count) return false;
            if (recordTokens.Count >= 2)
            {
                var surname = recordTokens[^1];
                return heardTokens.Any(ht => TokensAgree(surname, ht));
            }
            return true;
        }

        internal static bool TokensAgree(string recordToken, string heardToken)
        {
            if (string.Equals(recordToken, heardToken, StringComparison.Ordinal)) return true;
            if (recordToken.Length < 5 || heardToken.Length < 5) return false;

            var distance = StringUtils.LevenshteinDistance(recordToken, heardToken);
            var ratio = 1.0 - (double)distance / Math.Max(recordToken.Length, heardToken.Length);
            if (ratio >= 0.8) return true;

            var sa = PhoneticSkeleton(recordToken);
            var sb = PhoneticSkeleton(heardToken);
            return sa.Length >= 2 && sa == sb;
        }

        /// <summary>Consonant skeleton with whisper's confusable sounds folded and repeats collapsed.</summary>
        internal static string PhoneticSkeleton(string token)
        {
            var sb = new System.Text.StringBuilder(token.Length);
            char last = '\0';
            foreach (var ch in token.ToLowerInvariant())
            {
                var folded = ch switch
                {
                    'b' or 'v' or 'p' or 'f' => 'b',
                    'd' or 't' => 'd',
                    'k' or 'c' or 'g' or 'q' or 'j' or 'x' => 'k',
                    's' or 'z' => 's',
                    'm' or 'n' => 'n',
                    'l' or 'r' => 'l',
                    _ => '\0',
                };
                if (folded == '\0' || folded == last) continue;
                sb.Append(folded);
                last = folded;
            }
            return sb.ToString();
        }

        private static bool TitleAgrees(Audiobook book, string heardTitle)
        {
            // The verifier already scored the title with numeral canonicalization
            // ("Sixty-One Hours" ↔ "61 Hours"); prefer its answer when present.
            var verdict = VerificationDetailSerializer.TryDeserialize(book.VerificationDetailJson);
            if (verdict?.TitleMatch is { } scored) return scored.Score >= 0.7;
            var a = TitleMatcher.Normalize(book.Title);
            var b = TitleMatcher.Normalize(heardTitle);
            return a.Length > 0 && b.Length > 0
                && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
        }
    }
}
