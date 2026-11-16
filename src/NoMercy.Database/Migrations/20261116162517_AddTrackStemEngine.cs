using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackStemEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "TrackStems",
                type: "TEXT",
                maxLength: 16,
                nullable: true
            );

            // Every stem the server split before this column was a Spleeter
            // stem, registered as spleeter-...@<ffmpeg>. A plugin's own
            // producer version says nothing, and its rows stay unknown.
            migrationBuilder.Sql(
                "UPDATE \"TrackStems\" SET \"Engine\" = 'spleeter' WHERE \"ProducerVersion\" LIKE 'spleeter-%';"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Engine", table: "TrackStems");
        }
    }
}
