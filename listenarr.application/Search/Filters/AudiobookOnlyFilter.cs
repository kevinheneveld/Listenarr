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

using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Filters out clearly non-audio product pages (paperback/hardcover/kindle) unless
/// there is explicit evidence this is an audiobook (runtime, narrator, or trusted metadata source).
/// </summary>
public class AudiobookOnlyFilter : ISearchResultFilter
{
    public string FilterReason => "non_audiobook_filtered";

    // Music-scene release naming: a release-type token (SINGLE/EP/ALBUM/…) immediately followed by a
    // medium (WEB/CD/VINYL/FLAC/…), e.g. "...-Clash of the Titans-[IVT075]-SINGLE-WEB-2026-PTC". That
    // pairing is the scene grammar for music releases and does not occur in audiobook release titles,
    // so — with no positive audio signal — it marks a music single/album that matched an audiobook
    // title by chance (a real live case: a techno single grabbed for the generic audiobook "Titans").
    // Deliberately kept to the *delimited format-pair* rather than bare "SINGLE" or "WEB" so ordinary
    // titles ("Single Malt", a "...WEB..." audiobook web rip) are not caught.
    private static readonly Regex MusicSceneReleasePattern = new(
        @"\b(SINGLE|EP|ALBUM|CDM|CDS|CDEP|MCD|VLS|CDA|VINYL|LP|MAXI)[-_ .](WEB|CD|CDDA|VINYL|FLAC|CABLE|SAT|DAB|DVBC|MP3)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool ShouldFilter(SearchResult result)
    {
        // If enriched with a metadata source, prefer that metadata only when the
        // metadata source is a trusted audio provider or the enriched metadata
        // contains explicit audio signals (runtime or narrator).
        if (result.IsEnriched && !string.IsNullOrWhiteSpace(result.MetadataSource))
        {
            var source = result.MetadataSource;
            var trustedAudioSources = new[] { "Audible", "Audnexus" };

            var hasExplicitAudioFromMetadata = (result.Runtime.HasValue && result.Runtime.Value > 0)
                                               || !string.IsNullOrWhiteSpace(result.Narrator);

            var sourceIsTrusted = trustedAudioSources.Any(s => source.Contains(s, StringComparison.OrdinalIgnoreCase));

            if (sourceIsTrusted || hasExplicitAudioFromMetadata)
            {
                // Metadata indicates audio or comes from a trusted audio source - do not filter.
                return false;
            }

            // Otherwise, fall through and allow the normal print/box-set heuristics
            // to run against enriched Amazon-scraped metadata.
        }

        // Positive audiobook signals
        var hasRuntime = result.Runtime.HasValue && result.Runtime.Value > 0;
        var hasNarrator = !string.IsNullOrWhiteSpace(result.Narrator);
        var metadataIndicatesAudio = !string.IsNullOrWhiteSpace(result.MetadataSource) &&
                                     (result.MetadataSource.Contains("Audible", StringComparison.OrdinalIgnoreCase)
                                      || result.MetadataSource.Contains("Audnexus", StringComparison.OrdinalIgnoreCase)
                                      || result.MetadataSource.Contains("Amazon", StringComparison.OrdinalIgnoreCase));

        if (hasRuntime || hasNarrator || metadataIndicatesAudio)
        {
            return false;
        }

        // Negative print/kindle/box-set indicators in title/format. These tend to appear on product pages
        // for print/boxed editions (often accompanied by a format suffix like 'Paperback – <date>').
        var title = result.Title ?? string.Empty;
        var format = result.Format ?? string.Empty;

        var simpleIndicators = new[] { "Paperback", "Hardcover", "Mass Market Paperback", "eBook", "Kindle Edition", "Audio CD", "Board book" };
        var phraseIndicators = new[] { "Box Set", "3 Books", "3 Book", "3-Book", "Three Volume", "Three Volume Set", "Volume Set", "Trilogy", "Collector's Edition", "Slipcase", "Box Set:", "Box set:" };
        var suffixIndicators = new[] { "Paperback –", "Hardcover –", "Mass Market Paperback –" };

        // Non-audio media file-formats. A release whose title/format carries a comic, ebook, or video
        // file extension — with no positive audio signal (we already returned above if there was one) —
        // is not an audiobook, even when its title is otherwise relevant. This is the case that lets a
        // comic like "Tom Corbett Space Cadet V2 001(2013)(Digital)(TLK EMPIRE HD).cbr ( Nem )" get
        // mis-matched to an audiobook by title alone, grabbed, and then fail/import-block in a loop.
        // Matched as dotted extensions to stay tight: audiobook releases use .m4b/.mp3/.m4a/.flac, so
        // these never collide, and a legit "M4B + PDF" bundle is not caught (bare "PDF" is deliberately
        // omitted because audiobook bundles routinely include a PDF alongside the audio).
        var nonAudioFormatIndicators = new[]
        {
            ".cbr", ".cbz", ".cb7",            // comics
            ".epub", ".mobi", ".azw3", ".azw", // ebooks
            ".mkv", ".mp4", ".m4v", ".avi",    // video
        };

        bool HasAny(IEnumerable<string> patterns, string input) => patterns.Any(p => input.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

        var hasSimple = HasAny(simpleIndicators, title) || HasAny(simpleIndicators, format);
        var hasPhrase = HasAny(phraseIndicators, title) || HasAny(phraseIndicators, format);
        var hasSuffix = HasAny(suffixIndicators, title) || HasAny(suffixIndicators, format);
        var hasNonAudioFormat = HasAny(nonAudioFormatIndicators, title) || HasAny(nonAudioFormatIndicators, format);
        var hasMusicSceneFormat = MusicSceneReleasePattern.IsMatch(title) || MusicSceneReleasePattern.IsMatch(format);

        // If we see strong signals of a print/box-set/collection, a non-audio media format, or a
        // music-scene release, and we have no audio evidence, filter out.
        if (hasPhrase || hasSuffix || hasSimple || hasNonAudioFormat || hasMusicSceneFormat)
        {
            return true;
        }

        // Default: do not filter (let other filters decide)
        return false;
    }
}
