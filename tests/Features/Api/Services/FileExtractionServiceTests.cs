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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileExtractionServiceTests : IDisposable
    {
        private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "ListenarrFileExtractionTests", Guid.NewGuid().ToString("N"));
        private readonly List<ListenArrDbContext> _contexts = new();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, true);
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{_tempRoot}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{_tempRoot}': {ex.Message}");
            }

            foreach (var context in _contexts)
            {
                context.Dispose();
            }
        }

        [Theory]
        [InlineData("none", DuplicateStrategy.None)]
        [InlineData("None", DuplicateStrategy.None)]
        [InlineData("merge", DuplicateStrategy.Merge)]
        [InlineData("Merge", DuplicateStrategy.Merge)]
        [InlineData("duplicate", DuplicateStrategy.Duplicate)]
        [InlineData("Duplicate", DuplicateStrategy.Duplicate)]
        public void DuplicateStrategy_AcceptsBothLowercaseAndPascalCaseFromTheWire(string wireValue, DuplicateStrategy expected)
        {
            // The FE sends lowercase tokens by convention ('none'/'merge'/'duplicate'); the
            // standard JsonStringEnumConverter (declared on the enum) reads case-insensitively.
            // If this test fails the modal will silently fall back to DuplicateStrategy.None on
            // every request — that's the failure mode the explicit attribute defends against.
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var json = $"{{\"duplicateStrategy\":\"{wireValue}\",\"metadata\":{{\"title\":\"X\"}}}}";
            var parsed = JsonSerializer.Deserialize<ExtractFileRequest>(json, options);
            Assert.NotNull(parsed);
            Assert.Equal(expected, parsed!.DuplicateStrategy);
        }

        [Fact]
        public void DuplicateStrategy_SerializesAsPascalCaseString()
        {
            // FE TS types reflect the wire format. Lock the on-the-wire PascalCase shape so
            // any future change to the converter or naming policy surfaces here instead of as
            // a silent FE typecheck mismatch.
            var json = JsonSerializer.Serialize(DuplicateStrategy.Merge, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("\"Merge\"", json);
        }

        [Fact]
        public async Task ReadEmbedded_ReturnsTagsFromMetadataService()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var bookFolder = Path.Join(libraryRoot, "H.G. Wells", "The First Men in the Moon");
            Directory.CreateDirectory(bookFolder);
            var sourcePath = Path.Join(bookFolder, "Eye of the World.m4b");
            await File.WriteAllTextAsync(sourcePath, "fake");

            var (service, db, _, metadataMock, _) = BuildService();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = "The Eye of the World: Book One of The Wheel of Time",
                    Artist = "Robert Jordan",
                    Narrator = "Rosamund Pike",
                    Year = 2021,
                });

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "The First Men in the Moon",
                Authors = new List<string> { "H.G. Wells" },
                BasePath = bookFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var embedded = await service.ReadEmbeddedAsync(1, 11);

            Assert.NotNull(embedded);
            Assert.Equal(11, embedded!.FileId);
            Assert.Equal("The Eye of the World: Book One of The Wheel of Time", embedded.Title);
            Assert.Equal("Robert Jordan", embedded.Author);
            Assert.Equal("Rosamund Pike", embedded.Narrator);
            Assert.Equal(2021, embedded.Year);
        }

        [Fact]
        public async Task ReadEmbedded_ReturnsNull_WhenAudiobookOrFileMissing()
        {
            var (service, _, _, _, _) = BuildService();

            var missingAudiobook = await service.ReadEmbeddedAsync(99, 1);
            Assert.Null(missingAudiobook);
        }

        [Fact]
        public async Task Extract_CreatesNewAudiobook_ReassignsFile_AndMovesItOnDisk()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceBookFolder = Path.Join(libraryRoot, "H.G. Wells", "The First Men in the Moon");
            Directory.CreateDirectory(sourceBookFolder);
            var sourcePath = Path.Join(sourceBookFolder, "Eye of the World.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio bytes");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };

            var (service, db, repo, _, libraryAddMock) = BuildService(settings, libraryRoot);

            db.Audiobooks.Add(new Audiobook
            {
                Id = 7,
                Title = "The First Men in the Moon",
                Authors = new List<string> { "H.G. Wells" },
                BasePath = sourceBookFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 71, AudiobookId = 7, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            libraryAddMock.Setup(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<LibraryAddOperationRequest, CancellationToken>(async (request, _) =>
                {
                    var created = request.Metadata.ToAudiobook();
                    created.BasePath = request.DestinationPath;
                    await repo.AddAsync(created);
                    return new LibraryAddOperationResult { Added = true, Audiobook = created };
                });

            var result = await service.ExtractToNewAudiobookAsync(7, 71, new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "The Eye of the World",
                    Authors = new List<string> { "Robert Jordan" },
                    Asin = "B002UZJBA8",
                },
            });

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.DestinationAudiobookId);
            Assert.NotEqual(7, result.DestinationAudiobookId);

            // File moved on disk to the new audiobook's folder, source folder no longer has it
            Assert.False(File.Exists(sourcePath));
            Assert.NotNull(result.NewFilePath);
            Assert.True(File.Exists(result.NewFilePath));
            Assert.Contains("Robert Jordan", result.NewFilePath);
            Assert.Contains("The Eye of the World", result.NewFilePath);

            // File row reassigned to the new audiobook
            var sourceAudiobook = await repo.GetByIdAsync(7);
            Assert.NotNull(sourceAudiobook);
            Assert.Empty(sourceAudiobook!.Files ?? new List<AudiobookFile>());
            Assert.True(result.SourceAudiobookEmpty);

            var destinationAudiobook = await repo.GetByIdAsync(result.DestinationAudiobookId!.Value);
            Assert.NotNull(destinationAudiobook);
            Assert.Single(destinationAudiobook!.Files!);
            Assert.Equal(71, destinationAudiobook.Files![0].Id);
        }

        [Fact]
        public async Task Extract_ReturnsConflict_WhenAsinMatchesExistingAndStrategyIsNone()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Misfiled");
            Directory.CreateDirectory(sourceFolder);
            var sourcePath = Path.Join(sourceFolder, "Eye.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");

            var (service, db, _, _, libraryAddMock) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "Wrong Parent",
                BasePath = sourceFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = sourcePath, Format = "m4b" }
                }
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 2,
                Title = "The Eye of the World",
                Authors = new List<string> { "Robert Jordan" },
                Asin = "B002UZJBA8",
                BasePath = Path.Join(libraryRoot, "Robert Jordan", "The Eye of the World"),
            });
            await db.SaveChangesAsync();

            var result = await service.ExtractToNewAudiobookAsync(1, 11, new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "The Eye of the World",
                    Authors = new List<string> { "Robert Jordan" },
                    Asin = "B002UZJBA8",
                },
            });

            Assert.False(result.Success);
            Assert.NotNull(result.Conflict);
            Assert.Equal(2, result.Conflict!.ExistingAudiobookId);
            Assert.Equal("merge", result.Conflict.RecommendedStrategy); // existing has 0 files → recommend merge
            libraryAddMock.Verify(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.True(File.Exists(sourcePath)); // source file untouched
        }

        [Fact]
        public async Task Extract_MergesFileIntoExistingAudiobook_WhenStrategyIsMerge()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Misfiled");
            var destFolder = Path.Join(libraryRoot, "Robert Jordan", "The Eye of the World");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(destFolder);
            var sourcePath = Path.Join(sourceFolder, "Eye.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };

            var (service, db, repo, _, libraryAddMock) = BuildService(settings, libraryRoot);

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "Wrong Parent",
                BasePath = sourceFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = sourcePath, Format = "m4b" }
                }
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 2,
                Title = "The Eye of the World",
                Authors = new List<string> { "Robert Jordan" },
                Asin = "B002UZJBA8",
                BasePath = destFolder,
            });
            await db.SaveChangesAsync();

            var result = await service.ExtractToNewAudiobookAsync(1, 11, new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "The Eye of the World",
                    Authors = new List<string> { "Robert Jordan" },
                    Asin = "B002UZJBA8",
                },
                DuplicateStrategy = DuplicateStrategy.Merge,
            });

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.DestinationAudiobookId);
            Assert.Equal(DuplicateStrategy.Merge, result.AppliedStrategy);
            libraryAddMock.Verify(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(result.NewFilePath!));
            Assert.StartsWith(destFolder, result.NewFilePath!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Extract_RefusesToOverwriteExistingFileAtTarget()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Source");
            var destFolder = Path.Join(libraryRoot, "Robert Jordan", "The Eye of the World");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(destFolder);
            var sourcePath = Path.Join(sourceFolder, "Eye.m4b");
            var collisionPath = Path.Join(destFolder, "The Eye of the World.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");
            await File.WriteAllTextAsync(collisionPath, "collision");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };

            var (service, db, repo, _, libraryAddMock) = BuildService(settings, libraryRoot);

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "Source",
                BasePath = sourceFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            libraryAddMock.Setup(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<LibraryAddOperationRequest, CancellationToken>(async (request, _) =>
                {
                    var created = request.Metadata.ToAudiobook();
                    created.BasePath = request.DestinationPath;
                    await repo.AddAsync(created);
                    return new LibraryAddOperationResult { Added = true, Audiobook = created };
                });

            var result = await service.ExtractToNewAudiobookAsync(1, 11, new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "The Eye of the World",
                    Authors = new List<string> { "Robert Jordan" },
                },
            });

            Assert.False(result.Success);
            Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourcePath)); // source untouched
            Assert.True(File.Exists(collisionPath)); // collision untouched
            // Rolled-back destination audiobook should not be present (we deleted it after failure)
            // Source audiobook still owns the file
            var sourceAudiobook = await repo.GetByIdAsync(1);
            Assert.NotNull(sourceAudiobook);
            Assert.Single(sourceAudiobook!.Files!);
        }

        [Fact]
        public async Task Extract_RollsBackDestinationAudiobook_WhenDiskMoveFails()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Source");
            Directory.CreateDirectory(sourceFolder);
            var sourcePath = Path.Join(sourceFolder, "Eye.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };

            var (service, db, repo, _, libraryAddMock) = BuildService(settings, libraryRoot, fileMoverFailsMove: true);

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "Source",
                BasePath = sourceFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            libraryAddMock.Setup(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<LibraryAddOperationRequest, CancellationToken>(async (request, _) =>
                {
                    var created = request.Metadata.ToAudiobook();
                    created.BasePath = request.DestinationPath;
                    await repo.AddAsync(created);
                    return new LibraryAddOperationResult { Added = true, Audiobook = created };
                });

            var initialAudiobookCount = await db.Audiobooks.CountAsync();

            var result = await service.ExtractToNewAudiobookAsync(1, 11, new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "The Eye of the World",
                    Authors = new List<string> { "Robert Jordan" },
                },
            });

            Assert.False(result.Success);
            Assert.True(File.Exists(sourcePath)); // file move failed, source intact
            // Destination audiobook was created then deleted; final count equals initial
            var finalAudiobookCount = await db.Audiobooks.CountAsync();
            Assert.Equal(initialAudiobookCount, finalAudiobookCount);
            // File row still belongs to source
            var sourceAudiobook = await repo.GetByIdAsync(1);
            Assert.Equal(1, sourceAudiobook!.Files!.Single().AudiobookId);
        }

        private (FileExtractionService Service, ListenArrDbContext Db, AudiobookRepository Repo, Mock<IMetadataService> MetadataMock, Mock<ILibraryAddService> LibraryAddMock) BuildService(
            ApplicationSettings? settings = null,
            string? libraryRoot = null,
            bool fileMoverFailsMove = false)
        {
            settings ??= new ApplicationSettings
            {
                OutputPath = _tempRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };
            libraryRoot ??= settings.OutputPath;

            var dbName = Guid.NewGuid().ToString();
            var db = CreateContext(dbName);

            var config = new Mock<IConfigurationService>();
            config.Setup(s => s.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var repo = new AudiobookRepository(db);
            var fileRepo = new EfAudiobookFileRepository(db);
            var historyRepo = new EfHistoryRepository(db);
            var fileNaming = new FileNamingService(config.Object, NullLogger<FileNamingService>.Instance);

            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(m => m.PerformActionOn(FileAction.Move, It.IsAny<string>(), It.IsAny<string>()))
                .Returns<FileAction, string, string>((_, source, dest) =>
                {
                    if (fileMoverFailsMove) return Task.FromResult(false);
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.Move(source, dest, true);
                    return Task.FromResult(true);
                });

            var rootFolderService = new Mock<IRootFolderService>();
            var rootFolder = new RootFolder { Id = 1, Path = libraryRoot, IsDefault = true, Name = "default" };
            rootFolderService.Setup(s => s.GetAllAsync()).ReturnsAsync(new List<RootFolder> { rootFolder });
            rootFolderService.Setup(s => s.GetDefaultAsync()).ReturnsAsync(rootFolder);

            var metadataService = new Mock<IMetadataService>();
            var libraryAddService = new Mock<ILibraryAddService>();

            var service = new FileExtractionService(
                repo,
                fileRepo,
                historyRepo,
                rootFolderService.Object,
                config.Object,
                fileNaming,
                fileMover.Object,
                metadataService.Object,
                libraryAddService.Object,
                NullLogger<FileExtractionService>.Instance);

            return (service, db, repo, metadataService, libraryAddService);
        }

        private ListenArrDbContext CreateContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;
            var context = new ListenArrDbContext(options);
            _contexts.Add(context);
            return context;
        }
    }
}
