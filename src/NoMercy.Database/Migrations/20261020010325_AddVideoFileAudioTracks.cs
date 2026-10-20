using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoFileAudioTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoFileAudioTracks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VideoFileId = table.Column<string>(type: "TEXT", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Profile = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelLayout = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    BitRateKbps = table.Column<long>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Uri = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoFileAudioTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoFileAudioTracks_VideoFiles_VideoFileId",
                        column: x => x.VideoFileId,
                        principalTable: "VideoFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoFileAudioTracks_Codec_Channels",
                table: "VideoFileAudioTracks",
                columns: new[] { "Codec", "Channels" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoFileAudioTracks_VideoFileId",
                table: "VideoFileAudioTracks",
                column: "VideoFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoFileAudioTracks");
        }
    }
}
