using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Fork-local (kevin/canary-live). The July fork migration AddAutomaticSearchTuning added
    /// AutomaticSearchIntervalHours / AutomaticSearchBookDelaySeconds / AutomaticSearchTitleOnlyFallback;
    /// upstream #714 (20261021212418_AddAutomaticSearchTiming) adds the first two again plus
    /// AutomaticSearchCooldownHours, and the title-only fallback is replaced by upstream's
    /// author-free retry (#1072). Retire the fork columns by renaming them out of the way so the
    /// upstream migration applies unchanged: a rename is a native, transactional SQLite ALTER
    /// (no table rebuild, no non-transactional PRAGMA), unlike dropping a column. The retired
    /// columns are unmapped and inert; the Designer's target model is the fork snapshot minus the three.
    /// </remarks>
    public partial class RetireForkAutomaticSearchTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings",
                newName: "Retired_AutomaticSearchBookDelaySeconds");

            migrationBuilder.RenameColumn(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings",
                newName: "Retired_AutomaticSearchIntervalHours");

            migrationBuilder.RenameColumn(
                name: "AutomaticSearchTitleOnlyFallback",
                table: "ApplicationSettings",
                newName: "Retired_AutomaticSearchTitleOnlyFallback");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Restores the July columns with their original defaults rather than renaming the retired
        /// ones back: downgrading through upstream's AddAutomaticSearchTiming rebuilds the table from
        /// this migration's target model, which does not map the retired columns, so they are gone
        /// by the time this Down runs.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<bool>(
                name: "AutomaticSearchTitleOnlyFallback",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }
    }
}
