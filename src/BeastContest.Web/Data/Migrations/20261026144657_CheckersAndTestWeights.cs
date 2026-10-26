using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeastContest.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CheckersAndTestWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Score",
                table: "Submissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing verdicts used equal test weights: keep their points-mode results unchanged.
            migrationBuilder.Sql("UPDATE \"Submissions\" SET \"Score\" = CASE WHEN \"Total\" > 0 THEN 100 * \"Passed\" / \"Total\" ELSE 0 END;");

            migrationBuilder.AddColumn<string>(
                name: "TestResultsJson",
                table: "Submissions",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<double>(
                name: "CheckerEpsilon",
                table: "Problems",
                type: "double precision",
                nullable: false,
                defaultValue: 9.9999999999999995E-07);

            migrationBuilder.AddColumn<string>(
                name: "CheckerMode",
                table: "Problems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "tokens");

            migrationBuilder.AddColumn<string>(
                name: "CheckerSource",
                table: "Problems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Score",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "TestResultsJson",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CheckerEpsilon",
                table: "Problems");

            migrationBuilder.DropColumn(
                name: "CheckerMode",
                table: "Problems");

            migrationBuilder.DropColumn(
                name: "CheckerSource",
                table: "Problems");
        }
    }
}
