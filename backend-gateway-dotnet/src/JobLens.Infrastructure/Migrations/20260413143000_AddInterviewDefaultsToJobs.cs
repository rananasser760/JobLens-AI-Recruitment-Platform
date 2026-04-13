using Microsoft.EntityFrameworkCore.Migrations;

namespace JobLens.Infrastructure.Migrations;

[Migration("20260413143000_AddInterviewDefaultsToJobs")]
public partial class AddInterviewDefaultsToJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "InterviewDefaultsJson",
            table: "Jobs",
            type: "nvarchar(max)",
            nullable: false,
            defaultValue: "{}");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "InterviewDefaultsJson",
            table: "Jobs");
    }
}
