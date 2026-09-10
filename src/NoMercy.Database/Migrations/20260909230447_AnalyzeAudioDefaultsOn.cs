using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AnalyzeAudioDefaultsOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "AnalyzeAudio",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            // A store default only reaches rows written from now on. Every music
            // library that already exists was created while the column could
            // only be off — nothing in the API or the UI could ever set it — so
            // an existing row means "never asked", not "said no". Turning them
            // on is what makes the promise reach installs that predate this.
            migrationBuilder.Sql("UPDATE Libraries SET AnalyzeAudio = 1 WHERE Type = 'music';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "AnalyzeAudio",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);
        }
    }
}
