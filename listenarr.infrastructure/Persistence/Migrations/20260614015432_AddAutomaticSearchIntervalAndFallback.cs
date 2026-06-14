using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchIntervalAndFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults must match the model defaults (24h, fallback on), not 0/false:
            // the singleton settings row already exists on live instances, so a 0/false
            // default would give an interval of 0 (clamped to 1h — far too frequent) and
            // silently disable the recall fallback there.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticSearchTitleOnlyFallback",
                table: "ApplicationSettings");
        }
    }
}
