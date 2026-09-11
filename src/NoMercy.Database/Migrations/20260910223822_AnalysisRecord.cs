using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DerivedAudio",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(
                        type: "TEXT",
                        maxLength: 32,
                        nullable: false
                    ),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "TEXT",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedAudio", x => x.Key);
                }
            );

            migrationBuilder.CreateTable(
                name: "TrackDjAnalysis",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProducerPluginId = table.Column<string>(type: "TEXT", nullable: false),
                    DjAnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseAnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(
                        type: "TEXT",
                        maxLength: 1024,
                        nullable: true
                    ),
                    DownbeatIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    BeatsPerBar = table.Column<int>(type: "INTEGER", nullable: false),
                    PhraseLengthBars = table.Column<int>(type: "INTEGER", nullable: false),
                    PhraseStartsMs = table.Column<string>(
                        type: "TEXT",
                        maxLength: 65536,
                        nullable: true
                    ),
                    VocalRegionsMs = table.Column<string>(
                        type: "TEXT",
                        maxLength: 65536,
                        nullable: true
                    ),
                    BarEnergy = table.Column<string>(
                        type: "TEXT",
                        maxLength: 65536,
                        nullable: true
                    ),
                    CuePoints = table.Column<string>(
                        type: "TEXT",
                        maxLength: 65536,
                        nullable: true
                    ),
                    Chords = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackDjAnalysis", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_TrackDjAnalysis_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "TrackStems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Coverage = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowStartMs = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowEndMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProducerVersion = table.Column<string>(
                        type: "TEXT",
                        maxLength: 64,
                        nullable: false
                    ),
                    CreatedAt = table.Column<DateTime>(
                        type: "TEXT",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackStems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackStems_DerivedAudio_StorageKey",
                        column: x => x.StorageKey,
                        principalTable: "DerivedAudio",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_TrackStems_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_TrackStems_StorageKey",
                table: "TrackStems",
                column: "StorageKey"
            );

            migrationBuilder.CreateIndex(
                name: "IX_TrackStems_TrackId_Kind_Coverage_ProducerVersion",
                table: "TrackStems",
                columns: new[] { "TrackId", "Kind", "Coverage", "ProducerVersion" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TrackDjAnalysis");

            migrationBuilder.DropTable(name: "TrackStems");

            migrationBuilder.DropTable(name: "DerivedAudio");
        }
    }
}
