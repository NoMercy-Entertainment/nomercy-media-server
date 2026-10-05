using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations.Queue
{
    /// <inheritdoc />
    public partial class QueueJobSharedInput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedInputKey",
                table: "QueueJobs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueueJobBlobs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Data = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueJobBlobs", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueueJobs_SharedInputKey",
                table: "QueueJobs",
                column: "SharedInputKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueueJobBlobs");

            migrationBuilder.DropIndex(
                name: "IX_QueueJobs_SharedInputKey",
                table: "QueueJobs");

            migrationBuilder.DropColumn(
                name: "SharedInputKey",
                table: "QueueJobs");
        }
    }
}
