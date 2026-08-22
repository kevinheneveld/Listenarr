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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Configuration.Paths
{
    public class RemotePathMappingService(
        IRemotePathMappingRepository remotePathMappingRepository,
        ILogger<RemotePathMappingService> logger,
        IMemoryCache cache) : IRemotePathMappingService
    {
        public async Task<List<RemotePathMapping>> GetAllAsync()
        {
            var all = await remotePathMappingRepository.GetAllAsync();
            return all.OrderBy(m => m.DownloadClientId).ThenBy(m => m.Name).ToList();
        }

        public async Task<RemotePathMapping?> GetByIdAsync(int id)
        {
            return await remotePathMappingRepository.GetByIdAsync(id);
        }

        // Serializes cache-miss loads: the parallel client-queue poll calls
        // translate for several clients at once, and two concurrent misses
        // would race on the same scoped DbContext.
        private static readonly SemaphoreSlim MappingLoadLock = new(1, 1);

        public async Task<List<RemotePathMapping>> GetPathMappingByClientAsync(DownloadClientConfiguration client)
        {
            // Writers already invalidate this key on every mutation — but the
            // read side never populated it, so every translated queue item
            // hit the DbContext. Under the parallel client-queue poll that
            // meant two threads on one context ("A second operation was
            // started on this context instance", ~130/day) surfacing as a
            // phantom "NZBGet client error" banner. Mappings change rarely;
            // the short TTL keeps even a missed invalidation harmless.
            var key = $"rpm_client_{client.Id}";
            if (cache.TryGetValue(key, out List<RemotePathMapping>? cached) && cached != null)
            {
                return cached;
            }

            await MappingLoadLock.WaitAsync();
            try
            {
                if (cache.TryGetValue(key, out cached) && cached != null)
                {
                    return cached;
                }
                var mappings = await remotePathMappingRepository.GetByClientIdAsync(client.Id);
                cache.Set(key, mappings, TimeSpan.FromMinutes(5));
                return mappings;
            }
            finally
            {
                MappingLoadLock.Release();
            }
        }

        public async Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping)
        {
            mapping.NormalizePaths();
            RequireUsableRemotePath(mapping.RemotePath);
            mapping.CreatedAt = DateTime.UtcNow;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation($"Created remote path mapping {saved.Id} for client {saved.DownloadClientId}: {saved.RemotePath} -> {saved.LocalPath}");

            try
            {
                cache.Remove($"rpm_client_{saved.DownloadClientId}");
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            return saved;
        }

        public async Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping)
        {
            var existing = await remotePathMappingRepository.GetByIdAsync(mapping.Id);
            if (existing == null)
            {
                throw new KeyNotFoundException($"Remote path mapping with ID {mapping.Id} not found");
            }

            mapping.NormalizePaths();
            RequireUsableRemotePath(mapping.RemotePath);
            mapping.CreatedAt = existing.CreatedAt;
            mapping.UpdatedAt = DateTime.UtcNow;

            var saved = await remotePathMappingRepository.SaveAsync(mapping);

            logger.LogInformation(
                "Updated remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
                saved.Id, saved.DownloadClientId, saved.RemotePath, saved.LocalPath);

            try { cache.Remove($"rpm_client_{saved.DownloadClientId}"); }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            return saved;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var existing = await remotePathMappingRepository.GetByIdAsync(id);
            if (existing == null) return false;

            var deleted = await remotePathMappingRepository.DeleteAsync(id);

            if (deleted)
            {
                logger.LogInformation(
                    "Deleted remote path mapping {MappingId} for client {ClientId}",
                    id, existing.DownloadClientId);

                try { cache.Remove($"rpm_client_{existing.DownloadClientId}"); }
                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return deleted;
        }

        public async Task<string> TranslatePathAsync(DownloadClientConfiguration client, string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                return remotePath;
            }

            var mappings = await GetPathMappingByClientAsync(client);
            foreach (var mapping in mappings)
            {
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        mapping.LocalPath,
                        out var localRoot,
                        out var localReason))
                {
                    logger.LogWarning(
                        "Remote path mapping {MappingId} has a local root that is unavailable on this host and was ignored for client {ClientId}: {Reason}",
                        mapping.Id,
                        client.Id,
                        localReason);
                    continue;
                }

                if (!TryGetRemoteSemantics(
                        mapping.RemotePath,
                        out var remoteSemantics))
                {
                    logger.LogWarning(
                        "Remote path mapping {MappingId} has ambiguous or non-absolute remote syntax and was ignored for client {ClientId}",
                        mapping.Id,
                        client.Id);
                    continue;
                }

                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    mapping.RemotePath,
                    remotePath,
                    remoteSemantics,
                    out var relativePath))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(relativePath))
                {
                    return FileUtils.EnsureTrailingSeparator(localRoot);
                }

                var remoteSeparators = remoteSemantics.Syntax == FileSystemPathSyntax.Windows
                    ? new[] { '\\', '/' }
                    : new[] { '/' };
                var localRelativePath = string.Join(
                    Path.DirectorySeparatorChar,
                    relativePath.Split(remoteSeparators, StringSplitOptions.RemoveEmptyEntries));
                if (FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    localRoot,
                    localRelativePath,
                    FileSystemPathSemantics.CurrentHostDefault,
                    out var mappedPath))
                {
                    return mappedPath;
                }

                logger.LogWarning(
                    "Remote path mapping {MappingId} produced an unsafe mapped path for client {ClientId}",
                    mapping.Id,
                    client.Id);
            }

            return remotePath;
        }

        private static void RequireUsableRemotePath(string remotePath)
        {
            if (!TryGetRemoteSemantics(remotePath, out _))
            {
                throw new ArgumentException(
                    "RemotePath must use an unambiguous absolute Windows or Unix path syntax.",
                    nameof(remotePath));
            }
        }

        private static bool TryGetRemoteSemantics(
            string remotePath,
            out FileSystemPathSemantics semantics)
        {
            if (remotePath.Length >= 3
                && char.IsLetter(remotePath[0])
                && remotePath[1] == ':'
                && remotePath[2] is '/' or '\\'
                || remotePath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                semantics = new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive);
                return true;
            }

            if (remotePath.StartsWith("//", StringComparison.Ordinal)
                || !remotePath.StartsWith("/", StringComparison.Ordinal))
            {
                semantics = default;
                return false;
            }

            semantics = new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive);
            return true;
        }
    }
}
