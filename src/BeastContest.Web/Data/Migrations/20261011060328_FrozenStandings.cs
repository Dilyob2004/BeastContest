using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeastContest.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FrozenStandings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FrozenStandingsJson",
                table: "Contests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StandingsFrozenAt",
                table: "Contests",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrozenStandingsJson",
                table: "Contests");

            migrationBuilder.DropColumn(
                name: "StandingsFrozenAt",
                table: "Contests");
        }
    }
}
