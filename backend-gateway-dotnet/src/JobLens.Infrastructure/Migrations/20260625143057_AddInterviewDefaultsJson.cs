using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobLens.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInterviewDefaultsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterviewDefaultsJson",
                table: "Jobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterviewDefaultsJson",
                table: "Jobs");
        }
    }
}
