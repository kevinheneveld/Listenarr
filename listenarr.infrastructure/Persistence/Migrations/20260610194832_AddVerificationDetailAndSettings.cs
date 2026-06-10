using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationDetailAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationDetailJson",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            // Defaults must match the model defaults (90s opening / 30s closing):
            // the singleton settings row already exists on live instances, so a 0
            // default would silently disable both sample windows.
            migrationBuilder.AddColumn<int>(
                name: "VerificationClosingSeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "VerificationOpeningSeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 90);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationDetailJson",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerificationClosingSeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VerificationOpeningSeconds",
                table: "ApplicationSettings");
        }
    }
}
