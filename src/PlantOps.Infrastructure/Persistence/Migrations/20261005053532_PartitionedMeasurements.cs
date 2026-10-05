using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartitionedMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MeasurementReadings",
                table: "MeasurementReadings");

            migrationBuilder.DropIndex(
                name: "IX_MeasurementReadings_PointId_SourceEventKey",
                table: "MeasurementReadings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reading_Quality",
                table: "MeasurementReadings");

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveDate",
                table: "MeasurementReadings",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.Sql("""
                UPDATE "MeasurementReadings" SET "EffectiveDate" = ("EffectiveAt" AT TIME ZONE 'UTC')::date;
                ALTER TABLE "MeasurementReadings" RENAME TO "MeasurementReadings_Legacy";
                CREATE TABLE "MeasurementReadings" (LIKE "MeasurementReadings_Legacy" INCLUDING DEFAULTS) PARTITION BY RANGE ("EffectiveDate");
                DO $migration$
                DECLARE row record; cmd text; indexes text[]; foreignkeys text[];
                BEGIN
                    SELECT array_agg(replace(indexdef, '"MeasurementReadings_Legacy"', '"MeasurementReadings"')) INTO indexes
                      FROM pg_indexes WHERE schemaname = current_schema() AND tablename = 'MeasurementReadings_Legacy';
                    SELECT array_agg(format('ALTER TABLE "MeasurementReadings" ADD CONSTRAINT %I %s', conname, pg_get_constraintdef(oid))) INTO foreignkeys
                      FROM pg_constraint WHERE conrelid = '"MeasurementReadings_Legacy"'::regclass AND contype = 'f';
                    FOR row IN SELECT DISTINCT "EffectiveDate" AS day FROM "MeasurementReadings_Legacy" LOOP
                        EXECUTE format('CREATE TABLE %I PARTITION OF "MeasurementReadings" FOR VALUES FROM (%L) TO (%L)', 'MeasurementReadings_' || to_char(row.day, 'YYYYMMDD'), row.day, row.day + 1);
                    END LOOP;
                    INSERT INTO "MeasurementReadings" SELECT * FROM "MeasurementReadings_Legacy";
                    DROP TABLE "MeasurementReadings_Legacy";
                    FOREACH cmd IN ARRAY coalesce(indexes, ARRAY[]::text[]) LOOP EXECUTE cmd; END LOOP;
                    FOREACH cmd IN ARRAY coalesce(foreignkeys, ARRAY[]::text[]) LOOP EXECUTE cmd; END LOOP;
                END $migration$;
                """);
            migrationBuilder.AddPrimaryKey(
                name: "PK_MeasurementReadings",
                table: "MeasurementReadings",
                columns: new[] { "EffectiveDate", "Id" });

            migrationBuilder.CreateTable(
                name: "DirtyMeasurementMinutes",
                columns: table => new
                {
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirtyMeasurementMinutes", x => new { x.PointId, x.BucketStart });
                });

            migrationBuilder.CreateTable(
                name: "IngestionGaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionGaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionGaps_MeasurementPoints_PointId",
                        column: x => x.PointId,
                        principalTable: "MeasurementPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementMinutes",
                columns: table => new
                {
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: true),
                    Max = table.Column<double>(type: "double precision", nullable: true),
                    Average = table.Column<double>(type: "double precision", nullable: true),
                    GoodCount = table.Column<int>(type: "integer", nullable: false),
                    UncertainCount = table.Column<int>(type: "integer", nullable: false),
                    BadCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    HasGap = table.Column<bool>(type: "boolean", nullable: false),
                    CoverageRatio = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementMinutes", x => new { x.PointId, x.BucketStart });
                    table.ForeignKey(
                        name: "FK_MeasurementMinutes_MeasurementPoints_PointId",
                        column: x => x.PointId,
                        principalTable: "MeasurementPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementPartitions",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AggregatedRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementPartitions", x => x.Date);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementReadings_EffectiveDate_PointId_SourceEventKey",
                table: "MeasurementReadings",
                columns: new[] { "EffectiveDate", "PointId", "SourceEventKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reading_Quality",
                table: "MeasurementReadings",
                sql: "\"EffectiveDate\" = (\"EffectiveAt\" AT TIME ZONE 'UTC')::date AND \"Quality\" IN ('Good','Uncertain','Bad') AND (\"NumericValue\" IS NULL OR (\"NumericValue\" <> 'NaN'::double precision AND \"NumericValue\" <> 'Infinity'::double precision AND \"NumericValue\" <> '-Infinity'::double precision))");

            migrationBuilder.Sql("""
                INSERT INTO "MeasurementPartitions" ("Date", "Revision", "AggregatedRevision") SELECT DISTINCT "EffectiveDate", 1, 0 FROM "MeasurementReadings";
                INSERT INTO "DirtyMeasurementMinutes" ("PointId", "BucketStart") SELECT DISTINCT "PointId", date_trunc('minute', "EffectiveAt", 'UTC') FROM "MeasurementReadings";
                """);
            migrationBuilder.CreateIndex(
                name: "IX_IngestionGaps_PointId_From",
                table: "IngestionGaps",
                columns: new[] { "PointId", "From" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Partition migration requires a backup restore to downgrade; retained raw data cannot be reconstructed.");
        }
    }
}
