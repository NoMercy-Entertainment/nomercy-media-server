using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddVisualizers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Visualizers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    License = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourceSha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 2560, nullable: false),
                    Textures = table.Column<string>(type: "TEXT", maxLength: 640, nullable: false),
                    Schema = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    RenderableWebgl2 = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 524288, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", rowVersion: true, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", rowVersion: true, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visualizers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VisualizerUsers",
                columns: table => new
                {
                    VisualizerId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChosenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisualizerUsers", x => new { x.VisualizerId, x.UserId });
                    table.ForeignKey(
                        name: "FK_VisualizerUsers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VisualizerUsers_Visualizers_VisualizerId",
                        column: x => x.VisualizerId,
                        principalTable: "Visualizers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Visualizers_Format",
                table: "Visualizers",
                column: "Format");

            migrationBuilder.CreateIndex(
                name: "IX_Visualizers_IsBuiltIn",
                table: "Visualizers",
                column: "IsBuiltIn");

            migrationBuilder.CreateIndex(
                name: "IX_Visualizers_Slug",
                table: "Visualizers",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisualizerUsers_UserId",
                table: "VisualizerUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualizerUsers_VisualizerId",
                table: "VisualizerUsers",
                column: "VisualizerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisualizerUsers");

            migrationBuilder.DropTable(
                name: "Visualizers");
        }
    }
}
