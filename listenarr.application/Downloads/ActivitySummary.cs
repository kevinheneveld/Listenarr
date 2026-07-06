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
using Listenarr.Domain.Downloads;

namespace Listenarr.Application.Downloads;

/// <summary>
/// The high-level buckets the Activity page summarizes downloads into. A single download maps
/// to exactly one category; a book (collapsed across its attempts) takes the category of its
/// most recent attempt.
/// </summary>
public enum ActivityCategory
{
    /// <summary>Queued / Downloading / Paused / Processing / ImportPending — actively moving toward the library.</summary>
    InProgress,
    /// <summary>ImportBlocked — finished downloading but the import needs attention (retryable).</summary>
    Blocked,
    /// <summary>Completed / Ready / Moved — the file made it into the library.</summary>
    Imported,
    /// <summary>Failed for a concrete reason (not a stall).</summary>
    Failed,
    /// <summary>Failed specifically because the download stalled (made no progress) and was reaped.</summary>
    Stalled,
}

/// <summary>One collapsed-per-book row for the Activity list.</summary>
public sealed class ActivityItem
{
    public string Id { get; set; } = string.Empty;
    public int? AudiobookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? Series { get; set; }
    public ActivityCategory Category { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Progress { get; set; }
    public long TotalSize { get; set; }
    public long DownloadedSize { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>The timestamp used for sorting and the 24h window: CompletedAt when present, else StartedAt.</summary>
    public DateTime ActivityAt { get; set; }
    /// <summary>Human-readable failure / block reason, when the category has one.</summary>
    public string? Reason { get; set; }
    /// <summary>How many download records collapsed into this row (repeated grab attempts for the same book).</summary>
    public int AttemptCount { get; set; }
    public string DownloadClientId { get; set; } = string.Empty;
    public string? DownloadClientName { get; set; }
    /// <summary>The download client implementation (e.g. "qbittorrent", "nzbget", "DDL"), for the client-type indicator.</summary>
    public string? DownloadClientType { get; set; }
    /// <summary>
    /// Per-attempt breakdown of the download records that collapsed into this row, newest-first.
    /// Lets the UI explain what the <see cref="AttemptCount"/> grab attempts were and why each failed.
    /// </summary>
    public List<ActivityAttempt> Attempts { get; set; } = new();
}

/// <summary>One historical grab attempt for a book, projected from a single underlying download record.</summary>
public sealed class ActivityAttempt
{
    public ActivityCategory Category { get; set; }
    /// <summary>The raw <see cref="DownloadStatus"/> name for this attempt (Downloading/Failed/ImportBlocked/Moved/...).</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>When this attempt last advanced — CompletedAt when present, else StartedAt.</summary>
    public DateTime At { get; set; }
    /// <summary>The failure / block reason for this attempt, when it has one (null for in-flight or successful attempts).</summary>
    public string? Reason { get; set; }
    public string? DownloadClientName { get; set; }
}

/// <summary>A grouped failure reason and how many books hit it within the window.</summary>
public sealed class ActivityReasonCount
{
    public string Reason { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>The count chips shown above the Activity list. Terminal buckets are windowed; InProgress/Blocked are not.</summary>
public sealed class ActivitySummaryCounts
{
    public int InProgress { get; set; }
    public int Blocked { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Stalled { get; set; }
    public int WindowHours { get; set; }
    public List<ActivityReasonCount> FailureReasons { get; set; } = new();
}

public sealed class ActivityResponse
{
    public ActivitySummaryCounts Summary { get; set; } = new();
    public List<ActivityItem> Items { get; set; } = new();
    /// <summary>Total items in the active view before paging (so the client can show "showing N of M").</summary>
    public int TotalItems { get; set; }
}

/// <summary>
/// Pure, testable logic for the Activity page summary + collapsed list.
///
/// Responsibilities:
///   * Classify each download into a single <see cref="ActivityCategory"/>.
///   * Collapse repeated attempts for the same book into one row (latest attempt wins).
///   * Produce stable, non-double-counted category counts, windowing the terminal buckets.
///   * Order the list newest-first and drop terminal states out of the default view.
///
/// Kept free of repository/HTTP concerns so it can be unit-tested directly.
/// </summary>
public static class ActivitySummary
{
    /// <summary>
    /// Substring that marks a Failed download as a stall (vs. a genuine failure). The stall-timer
    /// reaper writes "Reaped by stall timer: no download progress"; matching here keeps this logic
    /// self-contained, with no compile-time dependency on the reaper (which only exists downstream).
    /// Where no reaper runs, nothing carries this marker and the Stalled bucket simply reads zero.
    /// </summary>
    public const string StallReasonMarker = "stall timer";

    public static bool IsStalled(Download d) =>
        d.Status == DownloadStatus.Failed &&
        !string.IsNullOrEmpty(d.ErrorMessage) &&
        d.ErrorMessage.Contains(StallReasonMarker, StringComparison.OrdinalIgnoreCase);

    public static ActivityCategory Classify(Download d) => d.Status switch
    {
        DownloadStatus.ImportBlocked => ActivityCategory.Blocked,
        DownloadStatus.Completed or DownloadStatus.Ready or DownloadStatus.Moved => ActivityCategory.Imported,
        DownloadStatus.Failed => IsStalled(d) ? ActivityCategory.Stalled : ActivityCategory.Failed,
        _ => ActivityCategory.InProgress, // Queued, Downloading, Paused, Processing, ImportPending
    };

    public static DateTime ActivityTimestamp(Download d) => d.CompletedAt ?? d.StartedAt;

    private static bool IsTerminal(ActivityCategory c) =>
        c is ActivityCategory.Imported or ActivityCategory.Failed or ActivityCategory.Stalled;

    // A book is represented by its current live state, not its history: an attempt that is still
    // in flight (InProgress) or awaiting action (Blocked) always wins over a terminal attempt,
    // even a more-recently-finished one. Otherwise a failed re-grab that finished after an
    // active download started would bury the active download out of the default view.
    private static bool IsActive(ActivityCategory c) =>
        c is ActivityCategory.InProgress or ActivityCategory.Blocked;

    private static string GroupKey(Download d) =>
        d.AudiobookId.HasValue
            ? $"ab:{d.AudiobookId.Value}"
            : $"title:{(d.Title ?? string.Empty).Trim().ToLowerInvariant()}";

    /// <summary>
    /// Build the summary counts and the (filtered, paged) collapsed list.
    /// </summary>
    /// <param name="downloads">All in-scope download records (already filtered to enabled clients).</param>
    /// <param name="nowUtc">Current time, for windowing.</param>
    /// <param name="windowHours">Window for the terminal buckets (Imported/Failed/Stalled). Defaults to 24.</param>
    /// <param name="filter">When set, the list returns only this category (terminal categories windowed); when null, the list is the default in-progress + blocked view.</param>
    /// <param name="page">1-based page index for the list.</param>
    /// <param name="pageSize">Page size for the list.</param>
    /// <param name="clientNameResolver">Maps a download-client id to its display name.</param>
    /// <param name="clientTypeResolver">Maps a download-client id to its implementation type (e.g. "qbittorrent", "nzbget").</param>
    public static ActivityResponse Build(
        IEnumerable<Download> downloads,
        DateTime nowUtc,
        int windowHours = 24,
        ActivityCategory? filter = null,
        int page = 1,
        int pageSize = 100,
        Func<string, string?>? clientNameResolver = null,
        Func<string, string?>? clientTypeResolver = null)
    {
        var effectiveWindowHours = windowHours <= 0 ? 24 : windowHours;
        var cutoff = nowUtc - TimeSpan.FromHours(effectiveWindowHours);

        // Collapse per book. The representative is the book's current live state: prefer an
        // active (InProgress/Blocked) attempt over any terminal one, then break ties by recency.
        var collapsed = downloads
            .GroupBy(GroupKey)
            .Select(g =>
            {
                var rep = g
                    .OrderByDescending(d => IsActive(Classify(d)))
                    .ThenByDescending(ActivityTimestamp)
                    .First();
                var item = ToItem(rep, g.Count(), clientNameResolver, clientTypeResolver);
                item.Attempts = g
                    .OrderByDescending(ActivityTimestamp)
                    .Select(d => ToAttempt(d, clientNameResolver))
                    .ToList();
                return item;
            })
            .ToList();

        var counts = new ActivitySummaryCounts { WindowHours = effectiveWindowHours };
        foreach (var item in collapsed)
        {
            var inWindow = item.ActivityAt >= cutoff;
            switch (item.Category)
            {
                case ActivityCategory.InProgress: counts.InProgress++; break;
                case ActivityCategory.Blocked: counts.Blocked++; break;
                case ActivityCategory.Imported: if (inWindow) counts.Imported++; break;
                case ActivityCategory.Failed: if (inWindow) counts.Failed++; break;
                case ActivityCategory.Stalled: if (inWindow) counts.Stalled++; break;
            }
        }

        counts.FailureReasons = collapsed
            .Where(i => i.ActivityAt >= cutoff &&
                        (i.Category == ActivityCategory.Failed || i.Category == ActivityCategory.Stalled))
            .GroupBy(i => NormalizeReason(i.Reason))
            .Select(g => new ActivityReasonCount { Reason = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<ActivityItem> view;
        if (filter.HasValue)
        {
            view = collapsed.Where(i => i.Category == filter.Value);
            if (IsTerminal(filter.Value))
                view = view.Where(i => i.ActivityAt >= cutoff);
        }
        else
        {
            // Default view: only what is actively in progress. Every other category — including
            // Blocked, which on a churning pipeline can run to hundreds of recent rows and would
            // otherwise rebuild the very wall this view replaces — drops off until its chip is
            // clicked. The chip counts (Blocked stays all-time) keep those backlogs visible.
            view = collapsed.Where(i => i.Category == ActivityCategory.InProgress);
        }

        var ordered = view
            .OrderByDescending(i => i.ActivityAt)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize < 1 ? 100 : pageSize;
        var paged = ordered
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToList();

        return new ActivityResponse
        {
            Summary = counts,
            Items = paged,
            TotalItems = ordered.Count,
        };
    }

    private static ActivityItem ToItem(Download d, int attemptCount, Func<string, string?>? resolver, Func<string, string?>? typeResolver)
    {
        var category = Classify(d);
        return new ActivityItem
        {
            Id = d.Id,
            AudiobookId = d.AudiobookId,
            Title = d.Title,
            Artist = d.Artist,
            Series = d.Series,
            Category = category,
            Status = d.Status.ToString(),
            Progress = d.Progress,
            TotalSize = d.TotalSize,
            DownloadedSize = d.DownloadedSize,
            StartedAt = d.StartedAt,
            CompletedAt = d.CompletedAt,
            ActivityAt = ActivityTimestamp(d),
            Reason = ReasonFor(d, category),
            AttemptCount = attemptCount,
            DownloadClientId = d.DownloadClientId,
            DownloadClientName = d.DownloadClientId == "DDL"
                ? "Direct Download"
                : resolver?.Invoke(d.DownloadClientId),
            DownloadClientType = d.DownloadClientId == "DDL"
                ? "DDL"
                : typeResolver?.Invoke(d.DownloadClientId),
        };
    }

    private static ActivityAttempt ToAttempt(Download d, Func<string, string?>? resolver)
    {
        var category = Classify(d);
        return new ActivityAttempt
        {
            Category = category,
            Status = d.Status.ToString(),
            At = ActivityTimestamp(d),
            Reason = ReasonFor(d, category),
            DownloadClientName = d.DownloadClientId == "DDL"
                ? "Direct Download"
                : resolver?.Invoke(d.DownloadClientId),
        };
    }

    private static string? ReasonFor(Download d, ActivityCategory c) => c switch
    {
        ActivityCategory.Blocked => !string.IsNullOrWhiteSpace(d.ImportBlockReason)
            ? d.ImportBlockReason
            : (d.ImportBlockMessages is { Count: > 0 } m ? m[0] : null),
        ActivityCategory.Failed or ActivityCategory.Stalled => d.ErrorMessage,
        _ => null,
    };

    /// <summary>
    /// Collapse a reason to a stable grouping key. Most reasons carry a variable tail after a colon
    /// (a path, a client state, an id); keep the leading segment so "qBittorrent state: missingFiles"
    /// and "qBittorrent state: errored" group together.
    /// </summary>
    internal static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Unknown";
        var r = reason.Trim();
        var colon = r.IndexOf(':');
        if (colon > 0 && colon <= 60) return r.Substring(0, colon).Trim();
        return r.Length > 60 ? r.Substring(0, 60).Trim() : r;
    }
}
