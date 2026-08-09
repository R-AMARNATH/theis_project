using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarbonAware.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCycleResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CycleResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CycleId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObjectiveType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    WeightConfig = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CloudProvider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PredictedMoerGPerKwh = table.Column<double>(type: "float", nullable: true),
                    PredictedCostUsdPerHr = table.Column<double>(type: "float", nullable: true),
                    PredictedLatencyMs = table.Column<double>(type: "float", nullable: true),
                    PredictedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActualTimestampStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActualTimestampEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatencyActualSec = table.Column<double>(type: "float", nullable: true),
                    ExecutionTimeSec = table.Column<double>(type: "float", nullable: true),
                    ActualMoerGPerKwh = table.Column<double>(type: "float", nullable: true),
                    DeploymentSuccess = table.Column<bool>(type: "bit", nullable: true),
                    ErrorNotes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CycleResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CycleResults_CycleId_CloudProvider_Region",
                table: "CycleResults",
                columns: new[] { "CycleId", "CloudProvider", "Region" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CycleResults");
        }
    }
}
