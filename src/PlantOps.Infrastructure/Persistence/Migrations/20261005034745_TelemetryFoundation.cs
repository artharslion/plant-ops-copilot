using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Assets_PlantId_Id",
                table: "Assets",
                columns: new[] { "PlantId", "Id" });

            migrationBuilder.CreateTable(
                name: "DataSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionParameters = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                    table.UniqueConstraint("AK_DataSources_PlantId_Id", x => new { x.PlantId, x.Id });
                    table.ForeignKey(
                        name: "FK_DataSources_Plants_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanonicalUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValueType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedIntervalMs = table.Column<int>(type: "integer", nullable: false),
                    StaleAfterMs = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementPoints", x => x.Id);
                    table.UniqueConstraint("AK_MeasurementPoints_PlantId_AssetId_Id", x => new { x.PlantId, x.AssetId, x.Id });
                    table.UniqueConstraint("AK_MeasurementPoints_PlantId_Id", x => new { x.PlantId, x.Id });
                    table.CheckConstraint("CK_Point_Intervals", "\"ExpectedIntervalMs\" BETWEEN 1000 AND 60000 AND \"StaleAfterMs\" BETWEEN \"ExpectedIntervalMs\" AND 300000");
                    table.ForeignKey(
                        name: "FK_MeasurementPoints_Assets_PlantId_AssetId",
                        columns: x => new { x.PlantId, x.AssetId },
                        principalTable: "Assets",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PointLatestStates",
                columns: table => new
                {
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    LatestObservedReadingId = table.Column<Guid>(type: "uuid", nullable: true),
                    LatestObservedValue = table.Column<double>(type: "double precision", nullable: true),
                    LatestObservedQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LatestObservedEffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LatestObservedReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastGoodReadingId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastGoodValue = table.Column<double>(type: "double precision", nullable: true),
                    LastTrustedEffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastGoodReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointLatestStates", x => x.PointId);
                    table.ForeignKey(
                        name: "FK_PointLatestStates_MeasurementPoints_PointId",
                        column: x => x.PointId,
                        principalTable: "MeasurementPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RawUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Scale = table.Column<double>(type: "double precision", nullable: false),
                    Offset = table.Column<double>(type: "double precision", nullable: false),
                    SourceParameters = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMappings", x => x.Id);
                    table.UniqueConstraint("AK_SourceMappings_PlantId_PointId_Id", x => new { x.PlantId, x.PointId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceMappings_DataSources_PlantId_DataSourceId",
                        columns: x => new { x.PlantId, x.DataSourceId },
                        principalTable: "DataSources",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceMappings_MeasurementPoints_PlantId_PointId",
                        columns: x => new { x.PlantId, x.PointId },
                        principalTable: "MeasurementPoints",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumericValue = table.Column<double>(type: "double precision", nullable: true),
                    CanonicalUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RawValueJson = table.Column<string>(type: "jsonb", nullable: false),
                    RawUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMappingId = table.Column<Guid>(type: "uuid", nullable: false),
                    MappingVersion = table.Column<long>(type: "bigint", nullable: false),
                    RawStatusCode = table.Column<long>(type: "bigint", nullable: true),
                    StatusSymbol = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Quality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServerTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimestampOrigin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceEventKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeAnomaly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementReadings", x => x.Id);
                    table.CheckConstraint("CK_Reading_Quality", "\"Quality\" IN ('Good','Uncertain','Bad') AND (\"NumericValue\" IS NULL OR (\"NumericValue\" <> 'NaN'::double precision AND \"NumericValue\" <> 'Infinity'::double precision AND \"NumericValue\" <> '-Infinity'::double precision))");
                    table.ForeignKey(
                        name: "FK_MeasurementReadings_DataSources_PlantId_DataSourceId",
                        columns: x => new { x.PlantId, x.DataSourceId },
                        principalTable: "DataSources",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeasurementReadings_MeasurementPoints_PlantId_AssetId_Point~",
                        columns: x => new { x.PlantId, x.AssetId, x.PointId },
                        principalTable: "MeasurementPoints",
                        principalColumns: new[] { "PlantId", "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeasurementReadings_SourceMappings_PlantId_PointId_SourceMa~",
                        columns: x => new { x.PlantId, x.PointId, x.SourceMappingId },
                        principalTable: "SourceMappings",
                        principalColumns: new[] { "PlantId", "PointId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementPoints_AssetId_Code",
                table: "MeasurementPoints",
                columns: new[] { "AssetId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_PlantId_AssetId_PointId",
                table: "MeasurementReadings",
                columns: new[] { "PlantId", "AssetId", "PointId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_PlantId_DataSourceId",
                table: "MeasurementReadings",
                columns: new[] { "PlantId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_PlantId_PointId_SourceMappingId",
                table: "MeasurementReadings",
                columns: new[] { "PlantId", "PointId", "SourceMappingId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_PointId_EffectiveAt",
                table: "MeasurementReadings",
                columns: new[] { "PointId", "EffectiveAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_PointId_SourceEventKey",
                table: "MeasurementReadings",
                columns: new[] { "PointId", "SourceEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceMappings_PlantId_DataSourceId",
                table: "SourceMappings",
                columns: new[] { "PlantId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceMappings_PointId",
                table: "SourceMappings",
                column: "PointId",
                unique: true,
                filter: "\"Enabled\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeasurementReadings");

            migrationBuilder.DropTable(
                name: "PointLatestStates");

            migrationBuilder.DropTable(
                name: "SourceMappings");

            migrationBuilder.DropTable(
                name: "DataSources");

            migrationBuilder.DropTable(
                name: "MeasurementPoints");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Assets_PlantId_Id",
                table: "Assets");
        }
    }
}
