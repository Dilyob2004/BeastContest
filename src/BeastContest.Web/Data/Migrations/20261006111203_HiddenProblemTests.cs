using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeastContest.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class HiddenProblemTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProblemTests",
                columns: table => new
                {
                    ProblemId = table.Column<string>(type: "character varying(64)", nullable: false),
                    TestsJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProblemTests", x => x.ProblemId);
                    table.ForeignKey(
                        name: "FK_ProblemTests_Problems_ProblemId",
                        column: x => x.ProblemId,
                        principalTable: "Problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProblemTests");
        }
    }
}
