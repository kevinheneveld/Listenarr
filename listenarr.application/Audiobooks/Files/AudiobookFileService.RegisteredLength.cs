using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    // Registration file-length probe, split out of AudiobookFileService.cs to keep
    // that file under the architecture size cap.
    public partial class AudiobookFileService
    {
        /// <summary>
        /// Read the byte length of the file behind a registration metadata path.
        /// The pinned lease's metadata path is a /proc/self/fd magic link on
        /// Linux, and FileSystemInfo reads the link inode itself, whose reported
        /// size is a constant 64 — not the file's length. Opening the path
        /// follows the link to the pinned file, so the stream length is the real
        /// size of the exact object the lease holds open.
        /// </summary>
        private static long? TryGetRegisteredFileLength(string metadataPath)
        {
            try
            {
                using var stream = new FileStream(
                    metadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return stream.Length;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }
    }
}
