/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * Integration tests for MoveExecutor — the helper extracted from
 * MoveBackgroundService that does the actual copy-then-rename move.
 *
 * Why integration (real FS) and not pure unit: the bug we're fixing is a
 * filesystem interaction (recursive enumeration discovering its own output
 * when the staging directory lands inside the source tree). Mocking would
 * paint over exactly the surface area we need to verify. xUnit + the OS temp
 * directory are fast and deterministic enough.
 */
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Infrastructure.FileSystem;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem
{
    [Trait("Name", "MoveExecutorTests")]
    [Trait("Category", "MoveExecutor")]
    public class MoveExecutorTests : IDisposable
    {
        private readonly string _root;

        public MoveExecutorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lna-move-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
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
            var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        private static string WriteFile(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact(DisplayName = "Source-contains-target (the {Narrator} regression): copies once, source is gone, target has the files")]
        public async Task SourceContainsTarget_NoRecursion()
        {
            // /root/Author/Title/{file.m4b}     →   /root/Author/Title/Narrator/{file.m4b}
            // This is the configuration that triggered the catastrophic
            // recursion before the fix: targetParent == source, so the old
            // code placed the staging dir inside source and the recursive
            // enumeration kept finding its own output.
            var source = MakeDir("Author", "Title");
            WriteFile(source, "book.m4b", "audio-data");
            WriteFile(source, "cover.jpg", "image-data");
            var target = Path.Combine(source, "Narrator");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success, $"Move should succeed. Error: {outcome.ErrorMessage}");
            Assert.Equal(2, outcome.FilesCopied);
            Assert.False(outcome.TempPathInsideSource);
            Assert.True(Directory.Exists(target));
            Assert.True(File.Exists(Path.Combine(target, "book.m4b")));
            Assert.True(File.Exists(Path.Combine(target, "cover.jpg")));

            // Source's old layout (file.m4b at the parent level) is gone.
            // The target lives where source used to be; only the Narrator
            // subdir should remain at the old "Title" path.
            Assert.False(File.Exists(Path.Combine(source, "book.m4b")));
            Assert.False(File.Exists(Path.Combine(source, "cover.jpg")));
        }

        [Fact(DisplayName = "Sibling move: source and target unrelated → straightforward copy + delete source")]
        public async Task SiblingMove_Works()
        {
            var source = MakeDir("oldlayout", "Author", "Title");
            WriteFile(source, "book.m4b", "data");
            var subdir = MakeDir("oldlayout", "Author", "Title", "extras");
            WriteFile(subdir, "notes.txt", "notes");

            var target = Path.Combine(_root, "newlayout", "Author", "Title");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success, outcome.ErrorMessage);
            Assert.Equal(2, outcome.FilesCopied);
            Assert.True(File.Exists(Path.Combine(target, "book.m4b")));
            Assert.True(File.Exists(Path.Combine(target, "extras", "notes.txt")));
            Assert.False(Directory.Exists(source));
        }

        [Fact(DisplayName = "Source == target: no-op success, source untouched")]
        public async Task SourceEqualsTarget_NoOp()
        {
            var source = MakeDir("Author", "Title");
            WriteFile(source, "book.m4b", "data");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, source, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success);
            Assert.True(File.Exists(Path.Combine(source, "book.m4b")));
        }

        [Fact(DisplayName = "Target exists with content → refuses without touching anything")]
        public async Task NonEmptyTarget_Refuses()
        {
            var source = MakeDir("Author", "Title");
            WriteFile(source, "book.m4b", "fresh");
            var target = MakeDir("conflict", "Author", "Title");
            WriteFile(target, "preexisting.txt", "stay");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.False(outcome.Success);
            Assert.True(File.Exists(Path.Combine(source, "book.m4b")));
            Assert.True(File.Exists(Path.Combine(target, "preexisting.txt")));
        }

        [Fact(DisplayName = "Source at filesystem root with target inside it → refused with library-root message")]
        public async Task SourceAtFilesystemRoot_Refused()
        {
            // Simulate the bug from the 2026-05-26 organize run: an audiobook's
            // BasePath was stamped at the library root (e.g. "/audiobooks" as a
            // Docker mount), and organize-library tried to move it down a level
            // into "/audiobooks/Author/Title/Narrator". sourceContainsTarget is
            // true, so MoveExecutor would stage outside source — but source's
            // parent is "/" (or equivalent), which is unwritable. Worse, if the
            // copy *had* succeeded, Directory.Delete(source, true) would wipe
            // every other audiobook sharing the root.
            //
            // We can't actually use "/" as the test source (we don't own it
            // and don't want a "/" mkdir attempt). Instead we hand-build a path
            // string whose Path.GetDirectoryName resolves to "/", which is what
            // the production check guards on. Using a known no-such-dir source
            // would short-circuit on the Directory.Exists check, so we create
            // a real dir-at-root-of-tmpdir-mount substitute.
            //
            // Cross-platform: on Windows, Path.GetDirectoryName("C:\\foo")
            // returns "C:\\" which our IsFilesystemRoot detects via the 2-char
            // drive-letter form. On Linux, Path.GetDirectoryName("/foo") returns
            // "/". Test by passing a source whose computed parent (via Path
            // APIs) is the root.

            // Build a fake "root-mounted" source by reaching back to the
            // platform root and creating a subdir there is too invasive — so
            // we test the helper directly via a path crafted to have its
            // parent be the platform root form.
            // Linux only here; skip on other platforms.
            if (Path.DirectorySeparatorChar != '/')
            {
                return; // Skip on Windows — sandbox can't create at C:\\
            }

            // Use a non-existent /something-at-root as the source; expect early
            // "source does not exist" rejection rather than the root-parent
            // refusal. To exercise the root-parent path we need an actually-
            // existing source whose parent is "/". Skip if we can't create
            // such a dir (CI without root).
            string rootChild = "/lna-move-test-root-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(rootChild);
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                // Likely permission-denied — fine, that's the very environment
                // we're trying to guard against in prod. Skip the test.
                return;
            }

            try
            {
                File.WriteAllText(Path.Combine(rootChild, "book.m4b"), "data");
                var target = Path.Combine(rootChild, "Author", "Title");

                var outcome = await MoveExecutor.ExecuteMoveAsync(rootChild, target, Guid.NewGuid(), logger: null);

                Assert.False(outcome.Success);
                Assert.NotNull(outcome.ErrorMessage);
                Assert.Contains("root", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                // Source must be intact — refusing means not touching anything.
                Assert.True(File.Exists(Path.Combine(rootChild, "book.m4b")));
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

        [Fact(DisplayName = "Target-contains-source (flattening into ancestor) → refused")]
        public async Task TargetContainsSource_Refused()
        {
            var parent = MakeDir("Author", "Title");
            var source = MakeDir("Author", "Title", "Narrator");
            WriteFile(source, "book.m4b", "data");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, parent, Guid.NewGuid(), logger: null);

            Assert.False(outcome.Success);
            Assert.NotNull(outcome.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(source, "book.m4b")));
        }

        [Fact(DisplayName = "Source-contains-target with nested subdirs: full tree relocates correctly")]
        public async Task SourceContainsTarget_WithSubdirs()
        {
            var source = MakeDir("Author", "Title");
            WriteFile(source, "book.m4b", "main");
            var sub = MakeDir("Author", "Title", "extras");
            WriteFile(sub, "interview.m4b", "extra");
            var target = Path.Combine(source, "Narrator");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success, outcome.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(target, "book.m4b")));
            Assert.True(File.Exists(Path.Combine(target, "extras", "interview.m4b")));
        }

        [Fact(DisplayName = "Empty target dir gets reclaimed (left over from prior failed attempt)")]
        public async Task EmptyTarget_Reclaimed()
        {
            var source = MakeDir("Author", "Title");
            WriteFile(source, "book.m4b", "data");
            var target = MakeDir("newlayout", "Author", "Title"); // empty

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success, outcome.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(target, "book.m4b")));
            Assert.False(Directory.Exists(source));
        }

        [Fact(DisplayName = "Error message truncation caps absurd lengths")]
        public void ErrorTruncation()
        {
            var huge = new string('x', MoveExecutor.MaxErrorMessageLength * 3);
            var truncated = MoveExecutor.TruncateErrorMessage(huge);
            Assert.True(truncated.Length < huge.Length);
            Assert.True(truncated.Length <= MoveExecutor.MaxErrorMessageLength + 64);
            Assert.Contains("truncated", truncated);
        }

        [Fact(DisplayName = "Orphan-temp enumeration finds both new and legacy patterns")]
        public void OrphanEnumeration_FindsBoth()
        {
            var bookDir = MakeDir("Author", "Title");
            var legacyOrphan = Path.Combine(bookDir, $"Narrator.tmp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(legacyOrphan);

            var newOrphan = Path.Combine(_root, $".lna-move-{Guid.NewGuid():N}");
            Directory.CreateDirectory(newOrphan);

            var notAnOrphan = Path.Combine(bookDir, "regular-folder");
            Directory.CreateDirectory(notAnOrphan);

            var found = MoveExecutor.EnumerateOrphanTempDirs(_root).ToList();
            Assert.Contains(found, p => p == legacyOrphan);
            Assert.Contains(found, p => p == newOrphan);
            Assert.DoesNotContain(found, p => p == notAnOrphan);
        }

        [Fact(DisplayName = "File contents survive the move byte-for-byte")]
        public async Task FileContentsPreserved()
        {
            var source = MakeDir("Author", "Title");
            var payload = "binary\0safepayload";
            WriteFile(source, "book.m4b", payload);
            var target = Path.Combine(source, "Narrator");

            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, Guid.NewGuid(), logger: null);

            Assert.True(outcome.Success, outcome.ErrorMessage);
            var roundtrip = File.ReadAllText(Path.Combine(target, "book.m4b"));
            Assert.Equal(payload, roundtrip);
        }
    }
}
