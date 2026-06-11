using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationLowCpuPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default must match the model default (true): the singleton settings row
            // already exists on live instances, so a false default would silently turn
            // the low-priority behavior off there.
            migrationBuilder.AddColumn<bool>(
                name: "VerificationLowCpuPriority",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationLowCpuPriority",
                table: "ApplicationSettings");
        }
    }
}
