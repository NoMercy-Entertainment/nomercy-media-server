using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoMercy.Database.Migrations.Queue
{
    /// <inheritdoc />
    public partial class RemoveMusicAnalysisJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The server no longer analyses music, so the job class these rows
            // name is gone. Left in place, every one of them would be reserved,
            // fail to load and end up as a failed job after the update.
            migrationBuilder.Sql(
                "DELETE FROM QueueJobs WHERE Payload LIKE '%NoMercy.MediaProcessing.Jobs.MediaJobs.MusicAnalysisJob,%'");

            migrationBuilder.Sql(
                "DELETE FROM FailedJobs WHERE Payload LIKE '%NoMercy.MediaProcessing.Jobs.MediaJobs.MusicAnalysisJob,%'");

            // The two schedules that fed and cleaned up after it.
            migrationBuilder.Sql(
                "DELETE FROM CronJobs WHERE JobType IN ('audio-analysis-sweep', 'derived-audio-eviction')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to put back: an older server queues its own analysis again.
        }
    }
}
