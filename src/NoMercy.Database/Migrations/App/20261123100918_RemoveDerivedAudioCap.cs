using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations.App
{
    /// <inheritdoc />
    public partial class RemoveDerivedAudioCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The size cap of the derived-audio store. The store is gone, so
            // the setting has nothing left to limit.
            migrationBuilder.Sql("DELETE FROM Configuration WHERE Key = 'derivedAudioCapGb'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to put back: an older server falls back to its default cap.
        }
    }
}
