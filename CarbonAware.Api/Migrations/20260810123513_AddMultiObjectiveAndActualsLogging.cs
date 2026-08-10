using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarbonAware.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiObjectiveAndActualsLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActualCostSource",
                table: "CycleResults",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ActualCostUsdPerHr",
                table: "CycleResults",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActualMoerSource",
                table: "CycleResults",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AverageCostUsdPerHr",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AverageLatencyMs",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CandidateCount",
                table: "AdviceExecutions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HighestCostCloud",
                table: "AdviceExecutions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HighestCostRegion",
                table: "AdviceExecutions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HighestCostUsdPerHr",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HighestLatencyCloud",
                table: "AdviceExecutions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HighestLatencyMs",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HighestLatencyRegion",
                table: "AdviceExecutions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectiveType",
                table: "AdviceExecutions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RegionsDiffer",
                table: "AdviceExecutions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SelectedCompositeScore",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SelectedCostUsdPerHr",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SelectedLatencyMs",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SingleObjectiveCloud",
                table: "AdviceExecutions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SingleObjectiveRegion",
                table: "AdviceExecutions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WeightCarbon",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WeightCost",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WeightLatency",
                table: "AdviceExecutions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WeightProfile",
                table: "AdviceExecutions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CompositeScore",
                table: "AdviceCandidates",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CostIsLive",
                table: "AdviceCandidates",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostSource",
                table: "AdviceCandidates",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CostUsdPerHr",
                table: "AdviceCandidates",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Excluded",
                table: "AdviceCandidates",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExclusionReason",
                table: "AdviceCandidates",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LatencyMs",
                table: "AdviceCandidates",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatencySource",
                table: "AdviceCandidates",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualCostSource",
                table: "CycleResults");

            migrationBuilder.DropColumn(
                name: "ActualCostUsdPerHr",
                table: "CycleResults");

            migrationBuilder.DropColumn(
                name: "ActualMoerSource",
                table: "CycleResults");

            migrationBuilder.DropColumn(
                name: "AverageCostUsdPerHr",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "AverageLatencyMs",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "CandidateCount",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestCostCloud",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestCostRegion",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestCostUsdPerHr",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestLatencyCloud",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestLatencyMs",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "HighestLatencyRegion",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "ObjectiveType",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "RegionsDiffer",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "SelectedCompositeScore",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "SelectedCostUsdPerHr",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "SelectedLatencyMs",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "SingleObjectiveCloud",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "SingleObjectiveRegion",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "WeightCarbon",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "WeightCost",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "WeightLatency",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "WeightProfile",
                table: "AdviceExecutions");

            migrationBuilder.DropColumn(
                name: "CompositeScore",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "CostIsLive",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "CostSource",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "CostUsdPerHr",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "Excluded",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "ExclusionReason",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "AdviceCandidates");

            migrationBuilder.DropColumn(
                name: "LatencySource",
                table: "AdviceCandidates");
        }
    }
}
