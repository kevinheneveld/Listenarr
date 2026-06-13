using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchBookDelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default must match the model default (5), not 0: the singleton
            // settings row already exists on live instances, so a 0 default
            // would silently leave the new throttle disabled there — the exact
            // opposite of the intent (taming a large wanted-list sweep).
            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings");
        }
    }
}
