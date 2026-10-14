using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpcUaSamplingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SamplingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMappingId = table.Column<Guid>(type: "uuid", nullable: false),
                    MappingVersion = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RevisedSamplingIntervalMs = table.Column<double>(type: "double precision", nullable: true),
                    RevisedPublishingIntervalMs = table.Column<double>(type: "double precision", nullable: true),
                    RevisedQueueSize = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SamplingProfiles", x => x.Id);
                    table.CheckConstraint("CK_SamplingProfile_Values", "\"Mode\" IN ('Periodic','Changes','Unknown') AND (\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\") AND (\"Mode\" <> 'Periodic' OR (\"RevisedSamplingIntervalMs\" > 0 AND \"RevisedSamplingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedPublishingIntervalMs\" IS NULL OR (\"RevisedPublishingIntervalMs\" > 0 AND \"RevisedPublishingIntervalMs\" < 'Infinity'::double precision)) AND (\"RevisedQueueSize\" IS NULL OR \"RevisedQueueSize\" BETWEEN 1 AND 10000)");
                    table.ForeignKey(
                        name: "FK_SamplingProfiles_MeasurementPoints_PointId",
                        column: x => x.PointId,
                        principalTable: "MeasurementPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SamplingProfiles_PointId_EffectiveFrom",
                table: "SamplingProfiles",
                columns: new[] { "PointId", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SamplingProfiles");
        }
    }
}
