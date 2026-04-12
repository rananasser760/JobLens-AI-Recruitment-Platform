using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GP_Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddInterviewGatewaySessionIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IntegritySessionId",
                table: "InterviewSessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterviewBackendSessionId",
                table: "InterviewSessions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InterviewSessions_IntegritySessionId",
                table: "InterviewSessions",
                column: "IntegritySessionId");

            migrationBuilder.CreateIndex(
                name: "IX_InterviewSessions_InterviewBackendSessionId",
                table: "InterviewSessions",
                column: "InterviewBackendSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InterviewSessions_IntegritySessionId",
                table: "InterviewSessions");

            migrationBuilder.DropIndex(
                name: "IX_InterviewSessions_InterviewBackendSessionId",
                table: "InterviewSessions");

            migrationBuilder.DropColumn(
                name: "IntegritySessionId",
                table: "InterviewSessions");

            migrationBuilder.DropColumn(
                name: "InterviewBackendSessionId",
                table: "InterviewSessions");
        }
    }
}
