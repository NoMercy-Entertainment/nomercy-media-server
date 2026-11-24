using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMusicAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackAudioAnalysis");

            migrationBuilder.DropTable(
                name: "TrackDjAnalysis");

            migrationBuilder.DropTable(
                name: "TrackStems");

            migrationBuilder.DropTable(
                name: "DerivedAudio");

            migrationBuilder.DropColumn(
                name: "AnalyzeAudio",
                table: "Libraries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnalyzeAudio",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DerivedAudio",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedAudio", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TrackAudioAnalysis",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BeatIntervalMs = table.Column<double>(type: "REAL", nullable: true),
                    BeatOffsetMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    BpmConfidence = table.Column<double>(type: "REAL", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    IntegratedLufs = table.Column<double>(type: "REAL", nullable: true),
                    IntroEndMs = table.Column<int>(type: "INTEGER", nullable: true),
                    KeyCamelot = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    KeyConfidence = table.Column<double>(type: "REAL", nullable: true),
                    KeyName = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    LoudnessRange = table.Column<double>(type: "REAL", nullable: true),
                    OutroStartMs = table.Column<int>(type: "INTEGER", nullable: true),
                    SpectralCentroid = table.Column<double>(type: "REAL", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    TruePeakDb = table.Column<double>(type: "REAL", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "TrackDjAnalysis",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BarEnergy = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    BaseAnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BeatsPerBar = table.Column<int>(type: "INTEGER", nullable: false),
                    Chords = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    CuePoints = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    DjAnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DownbeatIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PhraseLengthBars = table.Column<int>(type: "INTEGER", nullable: false),
                    PhraseStartsMs = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    ProducerPluginId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    VocalRegionsMs = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackDjAnalysis", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_TrackDjAnalysis_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackStems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Coverage = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Engine = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProducerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowEndMs = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowStartMs = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackStems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackStems_DerivedAudio_StorageKey",
                        column: x => x.StorageKey,
                        principalTable: "DerivedAudio",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackStems_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackStems_StorageKey",
                table: "TrackStems",
                column: "StorageKey");

            migrationBuilder.CreateIndex(
                name: "IX_TrackStems_TrackId_Kind_Coverage_ProducerVersion",
                table: "TrackStems",
                columns: new[] { "TrackId", "Kind", "Coverage", "ProducerVersion" },
                unique: true);
        }
    }
}
