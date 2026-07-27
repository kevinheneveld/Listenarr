/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent;

internal static class QbittorrentTorrentAddPlanner
{
    public static QbittorrentTorrentAddPlan Create(
        DownloadClientConfiguration client,
        PreparedTorrentSubmission submission)
    {
        var category = client.Settings?.TryGetValue("category", out var categoryValue) is true
            ? categoryValue?.ToString()
            : null;
        var tags = client.Settings?.TryGetValue("tags", out var tagsValue) is true
            ? tagsValue?.ToString()
            : null;

        return new QbittorrentTorrentAddPlan(
            submission.InfoHash,
            client.DownloadPath ?? string.Empty,
            category,
            tags,
            submission.TorrentBytes,
            submission.MagnetUri,
            submission.FileName,
            ReadDouble(client, "seedRatioLimit"),
            ReadInt(client, "seedTimeLimitMinutes"));
    }

    // Per-torrent seed limits from client settings: stamped onto every add so
    // Listenarr-grabbed torrents stop seeding at the configured point without
    // touching the client's global limits (which govern other apps' torrents
    // on a shared instance). Null/absent/garbage → no field sent → torrent
    // follows the client default.
    private static double? ReadDouble(DownloadClientConfiguration client, string key)
    {
        var raw = client.Settings?.TryGetValue(key, out var v) is true ? v?.ToString() : null;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static int? ReadInt(DownloadClientConfiguration client, string key)
    {
        var raw = client.Settings?.TryGetValue(key, out var v) is true ? v?.ToString() : null;
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }
}

internal sealed record QbittorrentTorrentAddPlan(
    string Hash,
    string SavePath,
    string? Category,
    string? Tags,
    byte[]? TorrentFileData,
    string? MagnetLink,
    string? FileName,
    double? SeedRatioLimit = null,
    int? SeedTimeLimitMinutes = null);
