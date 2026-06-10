using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "VerificationConfidence",
                table: "Audiobooks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationMethod",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationStatus",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VerificationTranscript",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedBy",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Audiobooks_VerificationStatus",
                table: "Audiobooks",
                column: "VerificationStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Audiobooks_VerificationStatus",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerificationConfidence",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerificationMethod",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerificationTranscript",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "VerifiedBy",
                table: "Audiobooks");
        }
    }
}
