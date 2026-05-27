/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * Safety guards on FileMover.MoveDirectoryAsync — the rename-flow analogue
 * of MoveExecutor's safeguards. The fallback path of MoveDirectoryAsync is
 * CopyDirRecursive(src, dst) + Directory.Delete(src, recursive: true), so
 * if any of these cases are unguarded:
 *   - src == dst                         → no-op, but used to fall through
 *   - dst inside src (target ⊂ source)   → recursive copy enumerates own output
 *   - src inside dst (source ⊂ target)   → delete of src damages the dst tree
 *   - src parent is filesystem root      → delete wipes everything sharing root
 *
 * RenameService is the only caller; this exercises FileMover directly so the
 * guards work even if a future caller bypasses RenameService's gates.
 */
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileMoverSafetyTests : IDisposable
    {
        private readonly string _root;
        private readonly IFileMover _mover;

        public FileMoverSafetyTests()
        {
            _root = Path.Join(Path.GetTempPath(), "lna_filemover_safety_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // FileMoverOptions defaults are fine for these tests — we never
            // reach the retry/fallback paths because the guards fail-fast.
            _mover = new FileMover(
                NullLogger<FileMover>.Instance,
                processRunner: null,
                options: Options.Create(new FileMoverOptions()));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }

        private string MakeDir(params string[] segments)
        {
            var path = Path.Join(new[] { _root }.Concat(segments).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact(DisplayName = "Empty source path → refuses, returns false")]
        public async Task EmptySource_Refused()
        {
            var dest = Path.Join(_root, "dest");
            Assert.False(await _mover.MoveDirectoryAsync("", dest));
            Assert.False(await _mover.MoveDirectoryAsync("   ", dest));
        }

        [Fact(DisplayName = "Empty destination path → refuses, returns false")]
        public async Task EmptyDest_Refused()
        {
            var src = MakeDir("src");
            File.WriteAllText(Path.Join(src, "f.txt"), "x");
            Assert.False(await _mover.MoveDirectoryAsync(src, ""));
            // Source must be untouched.
            Assert.True(File.Exists(Path.Join(src, "f.txt")));
        }

        [Fact(DisplayName = "Source == destination → no-op success")]
        public async Task SourceEqualsDest_NoOp()
        {
            var src = MakeDir("same");
            File.WriteAllText(Path.Join(src, "f.txt"), "x");

            Assert.True(await _mover.MoveDirectoryAsync(src, src));
            Assert.True(File.Exists(Path.Join(src, "f.txt"))); // untouched
        }

        [Fact(DisplayName = "Destination inside source → refused (would recurse and wipe result)")]
        public async Task DestInsideSource_Refused()
        {
            var src = MakeDir("Author", "Title");
            File.WriteAllText(Path.Join(src, "book.m4b"), "x");
            var dest = Path.Join(src, "Narrator"); // destination is a child of source

            Assert.False(await _mover.MoveDirectoryAsync(src, dest));
            // Source must remain intact — the guard fires before any FS work.
            Assert.True(File.Exists(Path.Join(src, "book.m4b")));
            Assert.False(Directory.Exists(dest));
        }

        [Fact(DisplayName = "Source inside destination → refused (flattening into ancestor)")]
        public async Task SourceInsideDest_Refused()
        {
            var ancestor = MakeDir("Author", "Title");
            var src = MakeDir("Author", "Title", "Narrator");
            File.WriteAllText(Path.Join(src, "book.m4b"), "x");

            Assert.False(await _mover.MoveDirectoryAsync(src, ancestor));
            Assert.True(File.Exists(Path.Join(src, "book.m4b")));
        }

        [Fact(DisplayName = "Source at filesystem root → refused (delete would wipe root)")]
        public async Task SourceAtFilesystemRoot_Refused()
        {
            // Linux-only: try to create a real /-child to exercise the guard.
            // On Windows / where mkdir at root is denied, skip — the guard
            // still applies via the same code path.
            if (Path.DirectorySeparatorChar != '/') return;

            var rootChild = "/lna-filemover-safety-" + Guid.NewGuid().ToString("N");
            try { Directory.CreateDirectory(rootChild); }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                return; // sandbox can't write to /, skip.
            }

            try
            {
                File.WriteAllText(Path.Join(rootChild, "f.txt"), "x");
                var dest = Path.Join(_root, "moved");

                Assert.False(await _mover.MoveDirectoryAsync(rootChild, dest));
                Assert.True(File.Exists(Path.Join(rootChild, "f.txt")));
            }
            finally
            {
                try { if (Directory.Exists(rootChild)) Directory.Delete(rootChild, true); }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }
        }

        [Fact(DisplayName = "Normal sibling-to-sibling move still works")]
        public async Task NormalSiblingMove_Works()
        {
            var src = MakeDir("a", "b");
            File.WriteAllText(Path.Join(src, "f.txt"), "x");
            var dest = Path.Join(_root, "c");

            Assert.True(await _mover.MoveDirectoryAsync(src, dest));
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dest, "f.txt")));
        }
    }
}
