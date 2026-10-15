using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations.Queue
{
    /// <inheritdoc />
    public partial class QueueJobOriginalPayloadHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalPayloadHash",
                table: "QueueJobs",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OriginalPayloadHash",
                table: "FailedJobs",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Existing rows: the handle they were given is their current hash.
            migrationBuilder.Sql("UPDATE QueueJobs SET OriginalPayloadHash = PayloadHash");

            migrationBuilder.CreateIndex(
                name: "IX_QueueJobs_OriginalPayloadHash",
                table: "QueueJobs",
                column: "OriginalPayloadHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueueJobs_OriginalPayloadHash",
                table: "QueueJobs");

            migrationBuilder.DropColumn(
                name: "OriginalPayloadHash",
                table: "QueueJobs");

            migrationBuilder.DropColumn(
                name: "OriginalPayloadHash",
                table: "FailedJobs");
        }
    }
}
