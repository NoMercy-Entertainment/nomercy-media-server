using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackLyricsProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LyricsSource",
                table: "Tracks",
                type: "TEXT",
                maxLength: 256,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "LyricsTiming",
                table: "Tracks",
                type: "TEXT",
                maxLength: 256,
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "LyricsVersion",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LyricsSource", table: "Tracks");

            migrationBuilder.DropColumn(name: "LyricsTiming", table: "Tracks");

            migrationBuilder.DropColumn(name: "LyricsVersion", table: "Tracks");
        }
    }
}
