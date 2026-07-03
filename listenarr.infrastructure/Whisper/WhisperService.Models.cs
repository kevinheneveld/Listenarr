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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Whisper
{
    /// <summary>
    /// Escalation-model resolution for the two-tier cascade: names from
    /// settings resolve to the baked tools directory, the persistent download
    /// cache on the config volume, or a single-flight first-use download.
    /// </summary>
    public partial class WhisperService
    {
        /// <summary>
        /// Resolve a model name (e.g. "small.en") to an on-disk ggml file:
        /// the configured first-pass model, the baked tools directory, the
        /// persistent download cache — or a first-use download into that cache.
        /// Null (with a warning) when none of those produce a usable file.
        /// </summary>
        private async Task<string?> ResolveModelPathAsync(string modelName, CancellationToken cancellationToken)
        {
            var fileName = ModelFileName(modelName);
            if (fileName == null)
            {
                _logger.LogWarning("Refusing invalid whisper model name {Model}", modelName.Length > 64 ? modelName[..64] : modelName);
                return null;
            }

            if (string.Equals(modelName, ModelName, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(_modelPath) ? _modelPath : null;
            }

            var bakedDir = Path.GetDirectoryName(_modelPath);
            var baked = bakedDir == null ? null : Path.Join(bakedDir, fileName);
            if (baked != null && File.Exists(baked)) return baked;

            var cached = Path.Join(_modelsDownloadDir, fileName);
            if (File.Exists(cached)) return cached;

            return await DownloadModelAsync(fileName, cached, cancellationToken);
        }

        private async Task<string?> DownloadModelAsync(string fileName, string destination, CancellationToken cancellationToken)
        {
            if (_httpClientFactory == null)
            {
                _logger.LogWarning("whisper model {File} not present and no HTTP client is available to download it", fileName);
                return null;
            }

            await ModelDownloadLock.WaitAsync(cancellationToken);
            try
            {
                // Another escalation may have completed the download while we waited.
                if (File.Exists(destination)) return destination;

                var url = string.Format(ModelDownloadUrlTemplate, fileName);
                _logger.LogInformation("Downloading whisper escalation model {File} from {Url}", fileName, url);

                Directory.CreateDirectory(_modelsDownloadDir);
                var tmpFile = destination + ".tmp";

                var client = _httpClientFactory.CreateClient("WhisperModels");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                var length = new FileInfo(tmpFile).Length;
                if (length < MinDownloadedModelBytes)
                {
                    _logger.LogWarning(
                        "Downloaded whisper model {File} is implausibly small ({Bytes} bytes) — discarding",
                        fileName, length);
                    File.Delete(tmpFile);
                    return null;
                }

                // Atomic publish: a crash mid-download leaves only the .tmp,
                // never a truncated file at the real path.
                File.Move(tmpFile, destination, overwrite: true);
                _logger.LogInformation("whisper escalation model {File} installed ({Mb} MB)", fileName, length / (1024 * 1024));
                return destination;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "whisper model download failed for {File} — escalation skipped", fileName);
                return null;
            }
            finally
            {
                ModelDownloadLock.Release();
            }
        }

        // Model names come from user settings — constrain to the ggml naming
        // alphabet so a hostile value can't smuggle path separators into the
        // filesystem or URL. Public + pure for unit testing.
        private static readonly Regex ModelNameRegex = new(@"^[a-z0-9][a-z0-9.\-]{0,40}$", RegexOptions.Compiled);

        public static string? ModelFileName(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return null;
            var normalized = modelName.Trim().ToLowerInvariant();
            return ModelNameRegex.IsMatch(normalized) ? $"ggml-{normalized}.bin" : null;
        }
    }
}
