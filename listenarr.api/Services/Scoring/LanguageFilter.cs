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

namespace Listenarr.Api.Services.Scoring
{
    /// <summary>
    /// Detects the language of a release title from explicit language markers,
    /// so the scorer can reject editions whose language is not among the
    /// profile's preferred languages. Conservative by design: it acts only on
    /// explicit markers (language names, bracketed/dashed country codes, foreign
    /// words for "audiobook", foreign words for "English"), never on a bare
    /// guess — a release tagged "[Английский]" (Russian for "English") is
    /// correctly detected as English. A title with no marker at all returns
    /// null (unknown) and is left alone.
    /// </summary>
    public static class LanguageFilter
    {
        // Non-English words that are safe to match as a bare standalone token —
        // they are not English words, so they can't appear innocently in an
        // English book title (unlike "Russian" in "The Russian"). Includes
        // native language names and foreign words for "audiobook".
        private static readonly Dictionary<string, string> BareForeignWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["espanol"] = "Spanish", ["español"] = "Spanish", ["castellano"] = "Spanish",
            ["deutsch"] = "German",
            ["francais"] = "French", ["français"] = "French",
            ["italiano"] = "Italian",
            ["polski"] = "Polish",
            ["русский"] = "Russian",
            ["nederlands"] = "Dutch",
            ["portugues"] = "Portuguese", ["português"] = "Portuguese",
            ["dansk"] = "Danish",
            ["svenska"] = "Swedish",
            ["norsk"] = "Norwegian",
            ["suomi"] = "Finnish",
            ["hörbuch"] = "German", ["horbuch"] = "German",
            ["audiolibro"] = "Spanish",
            ["luisterboek"] = "Dutch",
            ["lydbog"] = "Danish",
            ["ljudbok"] = "Swedish",
            ["lydbok"] = "Norwegian",
        };

        // Multi-word foreign phrases, matched as substrings.
        private static readonly Dictionary<string, string> BareForeignPhrases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["livre audio"] = "French",
        };

        // English names of languages. These ARE English words and routinely
        // appear in legitimate English titles ("The Russian", "French Kiss",
        // "Ancient Greek Literature"), so they only count as a language signal
        // when bracketed/parenthesised or in an "X Edition / X Version /
        // X Audiobook" construction — never bare.
        private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["spanish"] = "Spanish", ["german"] = "German", ["french"] = "French",
            ["italian"] = "Italian", ["polish"] = "Polish", ["russian"] = "Russian",
            ["dutch"] = "Dutch", ["portuguese"] = "Portuguese", ["danish"] = "Danish",
            ["swedish"] = "Swedish", ["norwegian"] = "Norwegian", ["finnish"] = "Finnish",
            ["czech"] = "Czech", ["japanese"] = "Japanese", ["chinese"] = "Chinese",
            ["korean"] = "Korean", ["hungarian"] = "Hungarian", ["turkish"] = "Turkish",
            ["greek"] = "Greek", ["hebrew"] = "Hebrew", ["arabic"] = "Arabic",
            ["romanian"] = "Romanian",
        };

        // Bracketed/parenthesised/dashed country-or-language codes, e.g. "[DE]",
        // "(FR)", "-PL-". Matched only in these delimited forms — a bare "de"
        // or "it" in a title is far too noisy to trust.
        private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "German", ["ger"] = "German",
            ["fr"] = "French", ["fre"] = "French",
            ["es"] = "Spanish", ["spa"] = "Spanish",
            ["it"] = "Italian", ["ita"] = "Italian",
            ["pl"] = "Polish", ["pol"] = "Polish",
            ["ru"] = "Russian", ["rus"] = "Russian",
            ["nl"] = "Dutch", ["dut"] = "Dutch",
            ["pt"] = "Portuguese", ["por"] = "Portuguese",
            ["dk"] = "Danish", ["dan"] = "Danish",
            ["se"] = "Swedish", ["swe"] = "Swedish",
            ["no"] = "Norwegian", ["nor"] = "Norwegian",
            ["fi"] = "Finnish", ["fin"] = "Finnish",
            ["cz"] = "Czech", ["cze"] = "Czech",
            ["jp"] = "Japanese", ["jpn"] = "Japanese",
            ["cn"] = "Chinese", ["chi"] = "Chinese",
        };

        // Explicit English markers — including foreign-language words for
        // "English" — that, when present, mean the content IS English even if
        // other foreign text appears in the title (foreign-tracker listings).
        private static readonly HashSet<string> EnglishMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            "english", "eng", "анг", "английский", "anglais", "ingles", "inglés",
            "englisch", "inglese", "angielski", "engelse", "engelsk",
        };

        private static readonly Regex WordRe = new(@"[\p{L}]+", RegexOptions.Compiled);
        // [xx] (xx) -xx- .xx. delimited code, 2-3 letters. Lookbehind/lookahead
        // so a shared delimiter (e.g. "-WEB-DK-") doesn't get consumed by the
        // first match and block the second.
        private static readonly Regex CodeRe = new(
            @"(?<=[\[\(\-\.])([a-z]{2,3})(?=[\]\)\-\.])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // A language name in a bracket/paren: "[Spanish]", "(German)".
        private static readonly Regex BracketedNameRe = new(
            @"[\[\(]\s*([a-z]+)\s*[\]\)]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // A language name in an "X Edition / X Version / X Audiobook" construction:
        // "Spanish Edition", "(Danish Edition)", "German audiobook".
        private static readonly Regex NameEditionRe = new(
            @"\b([a-z]+)\s+(?:edition|version|audiobook|audio book)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns the detected language name if the title carries an explicit
        /// language marker — "English" when an explicit English marker is
        /// present, otherwise the detected foreign language. Returns null when
        /// the title carries no confident marker at all.
        /// </summary>
        public static string? DetectLanguage(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var lower = title.ToLowerInvariant();

            // Explicit English marker anywhere → it's English, full stop.
            if (EnglishMarkerPresent(title))
                return "English";

            // Multi-word foreign phrases ("livre audio").
            foreach (var (phrase, lang) in BareForeignPhrases)
            {
                if (lower.Contains(phrase)) return lang;
            }

            // Bare foreign-language words — safe because they aren't English words.
            foreach (var w in WordRe.Matches(lower).Select(m => m.Value))
            {
                if (BareForeignWords.TryGetValue(w, out var lang))
                    return lang;
            }

            // Language NAMES (English words) only count when bracketed...
            foreach (Match m in BracketedNameRe.Matches(title))
            {
                if (LanguageNames.TryGetValue(m.Groups[1].Value, out var lang))
                    return lang;
            }
            // ...or in an "X Edition / X Version / X Audiobook" construction.
            foreach (Match m in NameEditionRe.Matches(title))
            {
                if (LanguageNames.TryGetValue(m.Groups[1].Value, out var lang))
                    return lang;
            }

            // Delimited country/language codes, e.g. "-DK-", "[PL]".
            foreach (Match m in CodeRe.Matches(title))
            {
                var code = m.Groups[1].Value.ToLowerInvariant();
                if (EnglishMarkers.Contains(code)) return "English";
                if (LanguageCodes.TryGetValue(code, out var lang))
                    return lang;
            }

            return null;
        }

        /// <summary>
        /// Maps an explicit language tag — an ISO code ("eng", "spa", "ger") or a
        /// language name ("English", "deutsch") — to a canonical language name.
        /// Returns null when the tag is empty or unrecognised, so callers can
        /// fail open. Unlike <see cref="DetectLanguage"/> this is for trusted
        /// tag fields (e.g. an ffprobe language tag), not free-text titles.
        /// </summary>
        public static string? MapLanguageTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag.Trim().ToLowerInvariant();
            if (EnglishMarkers.Contains(t)) return "English";
            if (LanguageCodes.TryGetValue(t, out var byCode)) return byCode;
            if (LanguageNames.TryGetValue(t, out var byName)) return byName;
            if (BareForeignWords.TryGetValue(t, out var byWord)) return byWord;
            return null;
        }

        private static bool EnglishMarkerPresent(string title)
        {
            var words = WordRe.Matches(title.ToLowerInvariant()).Select(m => m.Value);
            return words.Any(w => EnglishMarkers.Contains(w));
        }

        /// <summary>
        /// Decide whether a result should be rejected for language. Rejects when a
        /// language is detected in the title and it is not among the profile's
        /// preferred languages. Returns false (keep) when no preferred languages
        /// are configured or no language marker is detected.
        /// </summary>
        public static bool ShouldReject(string? title, IEnumerable<string>? preferredLanguages, out string? detected)
        {
            detected = null;
            var prefs = preferredLanguages?.Where(l => !string.IsNullOrWhiteSpace(l))
                                          .Select(l => l.Trim())
                                          .ToList();
            if (prefs == null || prefs.Count == 0)
                return false; // no language preference configured — don't filter

            var lang = DetectLanguage(title);
            if (lang == null)
                return false; // no confident language marker — leave it alone

            if (prefs.Any(p => p.Equals(lang, StringComparison.OrdinalIgnoreCase)))
                return false; // the detected language is explicitly wanted

            detected = lang;
            return true;
        }
    }
}
