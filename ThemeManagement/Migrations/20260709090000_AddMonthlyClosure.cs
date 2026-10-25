using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ThemeManagement.Data;

#nullable disable

namespace ThemeManagement.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260709090000_AddMonthlyClosure")]
    public partial class AddMonthlyClosure : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonthlyClosures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyClosures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyClosures_Year_Month",
                table: "MonthlyClosures",
                columns: new[] { "Year", "Month" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonthlyClosures");
        }
    }
}
