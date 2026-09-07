using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class TrackAudioAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnalyzeAudio",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TrackAudioAnalysis",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    BpmConfidence = table.Column<double>(type: "REAL", nullable: true),
                    BeatOffsetMs = table.Column<int>(type: "INTEGER", nullable: true),
                    BeatIntervalMs = table.Column<double>(type: "REAL", nullable: true),
                    KeyName = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    KeyCamelot = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    KeyConfidence = table.Column<double>(type: "REAL", nullable: true),
                    IntegratedLufs = table.Column<double>(type: "REAL", nullable: true),
                    TruePeakDb = table.Column<double>(type: "REAL", nullable: true),
                    LoudnessRange = table.Column<double>(type: "REAL", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    SpectralCentroid = table.Column<double>(type: "REAL", nullable: true),
                    IntroEndMs = table.Column<int>(type: "INTEGER", nullable: true),
                    OutroStartMs = table.Column<int>(type: "INTEGER", nullable: true),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackAudioAnalysis", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_TrackAudioAnalysis_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackAudioAnalysis");

            migrationBuilder.DropColumn(
                name: "AnalyzeAudio",
                table: "Libraries");
        }
    }
}
