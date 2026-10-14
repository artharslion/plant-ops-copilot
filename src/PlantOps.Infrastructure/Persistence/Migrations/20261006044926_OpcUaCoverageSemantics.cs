using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpcUaCoverageSemantics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SamplingProfile_Values",
                table: "SamplingProfiles");

            migrationBuilder.AlterColumn<double>(
                name: "CoverageRatio",
                table: "MeasurementMinutes",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<string>(
                name: "SamplingMode",
                table: "MeasurementMinutes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Periodic");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SamplingProfile_Values",
                table: "SamplingProfiles",
                sql: "\"Mode\" IN ('Periodic','Changes','Unknown') AND (\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\") AND (\"Mode\" <> 'Periodic' OR (\"RevisedSamplingIntervalMs\" IS NOT NULL AND \"RevisedSamplingIntervalMs\" > 0 AND \"RevisedSamplingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedPublishingIntervalMs\" IS NULL OR (\"RevisedPublishingIntervalMs\" > 0 AND \"RevisedPublishingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedQueueSize\" IS NULL OR \"RevisedQueueSize\" BETWEEN 1 AND 10000)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SamplingProfile_Values",
                table: "SamplingProfiles");

            migrationBuilder.DropColumn(
                name: "SamplingMode",
                table: "MeasurementMinutes");

            migrationBuilder.AlterColumn<double>(
                name: "CoverageRatio",
                table: "MeasurementMinutes",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SamplingProfile_Values",
                table: "SamplingProfiles",
                sql: "\"Mode\" IN ('Periodic','Changes','Unknown') AND (\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\") AND (\"Mode\" <> 'Periodic' OR (\"RevisedSamplingIntervalMs\" > 0 AND \"RevisedSamplingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedPublishingIntervalMs\" IS NULL OR (\"RevisedPublishingIntervalMs\" > 0 AND \"RevisedPublishingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedQueueSize\" IS NULL OR \"RevisedQueueSize\" BETWEEN 1 AND 10000)");
        }
    }
}
