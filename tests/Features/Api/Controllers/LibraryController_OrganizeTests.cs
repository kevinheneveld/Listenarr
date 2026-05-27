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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Api.Dtos;
using Listenarr.Domain.Models;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;

namespace Listenarr.Tests.Features.Api.Controllers
{
    /// <summary>
    /// Tests for the organize-library preview and apply endpoints. The
    /// preview buckets every audiobook into already_canonical / will_move /
    /// collision / invalid_target; apply queues a per-book move for each
    /// confirmed id through MoveQueueService.
    /// </summary>
    public class LibraryController_OrganizeTests : BaseTests
    {
        private const string Root = "/audiobooks";
        private readonly Mock<IImageCacheService> imageCacheServiceMock = new Mock<IImageCacheService>();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton(imageCacheServiceMock.Object);
            Init();
            await SeedSettingsAndRootAsync();
        }

        private async Task SeedSettingsAndRootAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithOutputPath(Root)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(Root)
                .WithIsDefault()
                .Build());
        }

        /// <summary>
        /// Attach a single tracked AudiobookFile to <paramref name="audiobook"/> so
        /// it survives <c>GetOrganizePreview</c>'s fileless-row skip filter (rows
        /// with zero tracked files are excluded from the preview entirely because
        /// they have nothing on disk to move). Tests that exercise bucket
        /// classification — already_canonical, will_move, collision,
        /// invalid_target — call this so the row reaches the bucketing logic.
        /// </summary>
        private Task AttachFileAsync(Audiobook audiobook, string? path = null)
        {
            return _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(path ?? $"{audiobook.BasePath ?? Root}/dummy.m4b")
                .Build());
        }

        [Fact]
        public async Task Preview_NoLibrary_ReturnsEmptyBuckets()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.GetOrganizePreview() as OkObjectResult;
            Assert.NotNull(actionResult);
            var preview = Assert.IsType<OrganizeLibraryPreviewDto>(actionResult!.Value);
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
            Assert.Equal(0, preview.CollisionCount);
        }

        [Fact]
        public async Task Preview_RowAtCanonicalPath_IsAlreadyCanonical()
        {
            var canonicalPath = $"{Root}/Author A/Book One";
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book One",
                Authors = new List<string> { "Author A" },
                BasePath = canonicalPath,
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.AlreadyCanonical, row.Status);
            Assert.Equal(1, preview.AlreadyCanonicalCount);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_RowWithDifferentPath_IsWillMove()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book Two",
                Authors = new List<string> { "Author B" },
                BasePath = $"{Root}/Misplaced",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.WillMove, row.Status);
            Assert.Equal($"{Root}/Author B/Book Two", row.TargetPath);
            Assert.Equal(1, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_TwoRowsTargetingSameFolder_AreCollision()
        {
            // Same {Author}/{Title} → same target path; different ASINs so the
            // existing dedup tool wouldn't catch them.
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Same Title",
                Authors = new List<string> { "Same Author" },
                Asin = "B00COLL0001",
                BasePath = $"{Root}/Old Place A",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Same Title",
                Authors = new List<string> { "Same Author" },
                Asin = "B00COLL0002",
                BasePath = $"{Root}/Old Place B",
            });
            await AttachFileAsync(ab1);
            await AttachFileAsync(ab2);

            var preview = await GetPreviewAsync();
            Assert.Equal(2, preview.Rows.Count);
            Assert.All(preview.Rows, r => Assert.Equal(OrganizePreviewStatus.Collision, r.Status));
            var key = preview.Rows[0].CollisionKey;
            Assert.False(string.IsNullOrEmpty(key));
            Assert.All(preview.Rows, r => Assert.Equal(key, r.CollisionKey));
            Assert.Equal(2, preview.CollisionCount);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_RowWithNoFilesAndNoBasePath_IsSkipped()
        {
            // Monitored-but-not-downloaded record: tracked in the library,
            // but nothing on disk and no destination set. Including it in
            // will_move would cause an "Source path does not exist" failure
            // when the user applies the default selection.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Wishlisted Book",
                Authors = new List<string> { "Author M" },
                BasePath = null,
            });

            var preview = await GetPreviewAsync();
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
            Assert.Equal(0, preview.AlreadyCanonicalCount);
            Assert.Equal(0, preview.CollisionCount);
            Assert.Equal(0, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_RowWithNoFilesButBasePathStamped_IsSkipped()
        {
            // Live-data variant: an earlier ingestion path stamped some
            // wishlist records' BasePath at the library root ("/audiobooks")
            // even though no files ever arrived. The narrow filter — empty
            // BasePath AND zero files — let these through; they appeared in
            // will_move, got default-selected, and each failed the move
            // queue's source-path-exists check. The loosened filter drops
            // every fileless row regardless of BasePath value.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Wishlisted Book",
                Authors = new List<string> { "Author N" },
                BasePath = Root, // i.e. "/audiobooks" — stamped, but no file ever landed
            });

            var preview = await GetPreviewAsync();
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_MissingAuthor_IsInvalidTarget()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Orphan Book",
                Authors = new List<string>(),
                BasePath = $"{Root}/Somewhere",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal("Missing author", row.Reason);
            Assert.Null(row.TargetPath);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_MissingTitle_IsInvalidTarget()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = null,
                Authors = new List<string> { "Author C" },
                BasePath = $"{Root}/Somewhere",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal("Missing title", row.Reason);
        }

        [Fact]
        public async Task Apply_EmptyBody_ReturnsBadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest());
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Apply_MissingIds_ReturnsBadRequestWithoutQueuing()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Existing Book",
                Authors = new List<string> { "Real Author" },
                BasePath = $"{Root}/Somewhere",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab.Id, 9999 },
            });

            Assert.IsType<BadRequestObjectResult>(result);
            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task Apply_QueuesMovesForWillMoveRows()
        {
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Me",
                Authors = new List<string> { "Author X" },
                BasePath = $"{Root}/Misplaced",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Me Too",
                Authors = new List<string> { "Author Y" },
                BasePath = $"{Root}/Other",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab1.Id, ab2.Id },
            }) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(2, result.Queued);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.FailedToQueue);
            Assert.Equal(2, result.QueuedJobs.Count);
            Assert.Contains(result.QueuedJobs, j => j.AudiobookId == ab1.Id && j.TargetPath == $"{Root}/Author X/Move Me");
            Assert.Contains(result.QueuedJobs, j => j.AudiobookId == ab2.Id && j.TargetPath == $"{Root}/Author Y/Move Me Too");

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Equal(2, jobs.Count);
            Assert.Contains(jobs, j => j.AudiobookId == ab1.Id && j.RequestedPath == $"{Root}/Author X/Move Me");
            Assert.Contains(jobs, j => j.AudiobookId == ab2.Id && j.RequestedPath == $"{Root}/Author Y/Move Me Too");
        }

        [Fact]
        public async Task Apply_SkipsRowsAlreadyAtCanonicalPath()
        {
            // User confirmed an id that — between preview and apply —
            // got moved to its target by something else. Apply should
            // skip with a warning, not enqueue a no-op.
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Already Done",
                Authors = new List<string> { "Author Z" },
                BasePath = $"{Root}/Author Z/Already Done",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab.Id },
            }) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(0, result.Queued);
            Assert.Equal(1, result.Skipped);
            Assert.Single(result.SkippedDetails, s => s.AudiobookId == ab.Id);

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task Apply_SkipsCollidingIdsInSameRequest()
        {
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Conflict",
                Authors = new List<string> { "Author Q" },
                BasePath = $"{Root}/Old A",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Conflict",
                Authors = new List<string> { "Author Q" },
                BasePath = $"{Root}/Old B",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab1.Id, ab2.Id },
            }) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(0, result.Queued);
            Assert.Equal(2, result.Skipped);
            Assert.NotEmpty(result.Warnings);

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        private async Task<OrganizeLibraryPreviewDto> GetPreviewAsync()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.GetOrganizePreview() as OkObjectResult;
            Assert.NotNull(actionResult);
            return Assert.IsType<OrganizeLibraryPreviewDto>(actionResult!.Value);
        }
    }
}
