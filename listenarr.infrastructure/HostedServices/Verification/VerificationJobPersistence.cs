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
using System.Text.Json;
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.HostedServices.Verification
{
    public class VerificationJobPersistence : IVerificationJobPersistence
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public VerificationJobPersistence(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task RecordQueuedAsync(Guid id, List<int>? audiobookIds, string trigger, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IVerificationJobRepository>();
            await repo.AddAsync(new VerificationJobRecord
            {
                Id = id,
                AudiobookIdsJson = audiobookIds == null ? null : JsonSerializer.Serialize(audiobookIds),
                Trigger = trigger,
                Status = "Queued",
                EnqueuedAt = DateTime.UtcNow
            }, ct);
        }

        public async Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IVerificationJobRepository>();
            await repo.SetStatusAsync(id, status, ct);
        }

        public async Task<List<VerificationJobRecord>> GetPendingAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IVerificationJobRepository>();
            return await repo.GetPendingAsync(ct);
        }

        public async Task CleanupAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IVerificationJobRepository>();
            await repo.DeleteFinishedOlderThanAsync(DateTime.UtcNow.AddDays(-7), ct);
        }
    }
}
