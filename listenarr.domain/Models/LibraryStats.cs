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
namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Aggregate metrics for the whole library, powering the dashboard. Composed
    /// of independent sections so each can later get its own endpoint or a
    /// drill-down query without reshaping the others.
    /// </summary>
    public class LibraryStats
    {
        public LibraryOverviewStats Overview { get; set; } = new();
        public MetadataCompletenessStats MetadataCompleteness { get; set; } = new();
        public SeriesStats Series { get; set; } = new();
        public AuthorStats Authors { get; set; } = new();
        public NarratorStats Narrators { get; set; } = new();
        public QualityStats Quality { get; set; } = new();
        public ActivityStats Activity { get; set; } = new();
        public List<GenreCount> TopGenres { get; set; } = new();
        public List<DurationBucket> DurationDistribution { get; set; } = new();
        public List<LanguageCount> Languages { get; set; } = new();
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class LibraryOverviewStats
    {
        public int TotalBooks { get; set; }

        /// <summary>Books whose file(s) are present on disk — the books actually owned.</summary>
        public int OwnedBooks { get; set; }

        /// <summary>Tracked books with no file present yet (wanted / not downloaded).</summary>
        public int MissingBooks { get; set; }

        public int MonitoredBooks { get; set; }
        public int UnmonitoredBooks { get; set; }
        public long TotalSizeBytes { get; set; }

        /// <summary>Total duration across owned books (hours).</summary>
        public double TotalDurationHours { get; set; }

        /// <summary>Average duration per owned book (hours).</summary>
        public double AverageDurationHours { get; set; }
    }

    /// <summary>
    /// Per-field counts of books missing a given piece of metadata. Each count
    /// is a candidate set for a future "backfill" action.
    /// </summary>
    public class MetadataCompletenessStats
    {
        public int TotalBooks { get; set; }
        public int MissingCoverArt { get; set; }
        public int MissingAsin { get; set; }
        public int MissingIsbn { get; set; }
        public int MissingGenres { get; set; }
        public int MissingNarrators { get; set; }
        public int MissingDescription { get; set; }
        public int MissingPublisher { get; set; }
        public int MissingLanguage { get; set; }
        public int MissingPublishDate { get; set; }
        public int MissingRuntime { get; set; }
        public int MissingSeriesPosition { get; set; }

        /// <summary>
        /// Average per-book completeness across all tracked fields, 0-100.
        /// </summary>
        public double OverallCompletenessPercent { get; set; }
    }

    /// <summary>
    /// Series completeness, computed by comparing owned books against the
    /// cached Audible series catalog. Series with no cached catalog are counted
    /// as "unknown" rather than guessed.
    /// </summary>
    public class SeriesStats
    {
        public int TotalSeries { get; set; }
        public int CompleteSeries { get; set; }
        public int IncompleteSeries { get; set; }
        public int UnknownCompletenessSeries { get; set; }
        public int BooksInSeries { get; set; }
        public int StandaloneBooks { get; set; }

        /// <summary>
        /// Books whose only "series" was a single-book Audible series, folded
        /// into StandaloneBooks rather than counted as a real series.
        /// </summary>
        public int SingleBookSeriesFolded { get; set; }

        /// <summary>
        /// Sum of catalog gaps across all series with a known catalog.
        /// </summary>
        public int MissingBooksAcrossSeries { get; set; }
    }

    public class AuthorStats
    {
        public int TotalAuthors { get; set; }
        public List<AuthorBookCount> TopAuthors { get; set; } = new();
    }

    public class AuthorBookCount
    {
        public string Author { get; set; } = string.Empty;

        /// <summary>Books by this author tracked in the library (owned or wanted).</summary>
        public int TotalBooks { get; set; }

        /// <summary>Of those, books whose file(s) are present on disk.</summary>
        public int OwnedBooks { get; set; }
    }

    public class NarratorStats
    {
        public int TotalNarrators { get; set; }
        public List<NarratorBookCount> TopNarrators { get; set; } = new();
    }

    public class NarratorBookCount
    {
        public string Narrator { get; set; } = string.Empty;

        /// <summary>Books with this narrator tracked in the library (owned or wanted).</summary>
        public int TotalBooks { get; set; }

        /// <summary>Of those, books whose file(s) are present on disk.</summary>
        public int OwnedBooks { get; set; }
    }

    public class QualityStats
    {
        public List<CodecCount> ByCodec { get; set; } = new();
        public List<BitrateBucket> ByBitrate { get; set; } = new();
    }

    public class CodecCount
    {
        public string Codec { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class BitrateBucket
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>Time granularity for the activity time-series.</summary>
    public enum ActivityGranularity
    {
        Day,
        Week,
        Month,
    }

    public class ActivityStats
    {
        /// <summary>Granularity the time-series buckets were computed at.</summary>
        public ActivityGranularity Granularity { get; set; } = ActivityGranularity.Month;

        /// <summary>
        /// Books added per period, oldest bucket first, one entry per period in
        /// the requested window (zero-filled).
        /// </summary>
        public List<ActivityBucket> BooksAddedByPeriod { get; set; } = new();

        public int TotalImports { get; set; }
        public int FailedImports { get; set; }

        /// <summary>Import success rate over recorded download history, 0-100.</summary>
        public double ImportSuccessRate { get; set; }
    }

    public class ActivityBucket
    {
        /// <summary>
        /// Human-readable bucket label: "yyyy-MM-dd" for day/week (week = start
        /// date), "yyyy-MM" for month.
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>UTC start of the bucket, inclusive.</summary>
        public DateTime PeriodStart { get; set; }

        public int Count { get; set; }
    }

    public class GenreCount
    {
        public string Genre { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class DurationBucket
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class LanguageCount
    {
        public string Language { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
