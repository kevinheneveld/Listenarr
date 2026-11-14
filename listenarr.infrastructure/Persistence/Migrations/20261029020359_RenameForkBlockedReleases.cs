using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Fork-local (kevin/canary-live). The July fork migration AddBlockedReleasesAndVerificationJobs
    /// created a "BlockedReleases" table (ReleaseTitle / TorrentHash, matched by title or hash) for
    /// wrong-content rejections, reaped stalls and terminal import failures. Upstream v1.7.0
    /// (20261029020400_AddReleaseBlocklist) creates a table of the same name with a different shape,
    /// keyed by release identity. Move the fork table aside so the upstream migration applies
    /// unchanged and the existing rows survive: a table rename is a native, transactional SQLite
    /// ALTER. The fork entity is now ForkBlockedRelease, and both lists are consulted on search.
    /// </remarks>
    public partial class RenameForkBlockedReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "BlockedReleases",
                newName: "ForkBlockedReleases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "ForkBlockedReleases",
                newName: "BlockedReleases");
        }
    }
}
