using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlarmFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlarmRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RuleType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RaiseAbove = table.Column<double>(type: "double precision", nullable: false),
                    ClearBelow = table.Column<double>(type: "double precision", nullable: false),
                    RaiseForMs = table.Column<int>(type: "integer", nullable: false),
                    ClearForMs = table.Column<int>(type: "integer", nullable: false),
                    MaxSampleGapMs = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmRules", x => x.Id);
                    table.UniqueConstraint("AK_AlarmRules_PlantId_PointId_Id", x => new { x.PlantId, x.PointId, x.Id });
                    table.CheckConstraint("CK_Rule_Threshold", "\"ClearBelow\" < \"RaiseAbove\" AND \"RaiseAbove\" > '-Infinity'::double precision AND \"RaiseAbove\" < 'Infinity'::double precision AND \"ClearBelow\" > '-Infinity'::double precision AND \"ClearBelow\" < 'Infinity'::double precision AND \"RaiseForMs\" BETWEEN 0 AND 300000 AND \"ClearForMs\" BETWEEN 0 AND 300000 AND \"MaxSampleGapMs\" BETWEEN 1 AND 300000 AND \"Severity\" IN ('Low','Medium','High') AND \"RuleType\" = 'HighThreshold'");
                    table.ForeignKey(
                        name: "FK_AlarmRules_MeasurementPoints_PlantId_PointId",
                        columns: x => new { x.PlantId, x.PointId },
                        principalTable: "MeasurementPoints",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Alarms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PointId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleVersion = table.Column<long>(type: "bigint", nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TriggerReadingId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerValue = table.Column<double>(type: "double precision", nullable: false),
                    ThresholdSnapshot = table.Column<string>(type: "jsonb", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alarms", x => x.Id);
                    table.CheckConstraint("CK_Alarm_State", "\"State\" IN ('Active','Recovered')");
                    table.ForeignKey(
                        name: "FK_Alarms_AlarmRules_PlantId_PointId_RuleId",
                        columns: x => new { x.PlantId, x.PointId, x.RuleId },
                        principalTable: "AlarmRules",
                        principalColumns: new[] { "PlantId", "PointId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Alarms_MeasurementPoints_PlantId_AssetId_PointId",
                        columns: x => new { x.PlantId, x.AssetId, x.PointId },
                        principalTable: "MeasurementPoints",
                        principalColumns: new[] { "PlantId", "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuleRuntimeStates",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PendingRaiseSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingClearSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTrustedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastInputTrusted = table.Column<bool>(type: "boolean", nullable: false),
                    MappingId = table.Column<Guid>(type: "uuid", nullable: true),
                    MappingVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    PointVersion = table.Column<long>(type: "bigint", nullable: false),
                    RuleVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleRuntimeStates", x => x.RuleId);
                    table.ForeignKey(
                        name: "FK_RuleRuntimeStates_AlarmRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AlarmRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ReadingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmEvents_Alarms_AlarmId",
                        column: x => x.AlarmId,
                        principalTable: "Alarms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_AlarmId_OccurredAt",
                table: "AlarmEvents",
                columns: new[] { "AlarmId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRules_PointId",
                table: "AlarmRules",
                column: "PointId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_PlantId_AssetId_PointId",
                table: "Alarms",
                columns: new[] { "PlantId", "AssetId", "PointId" });

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_PlantId_PointId_RuleId",
                table: "Alarms",
                columns: new[] { "PlantId", "PointId", "RuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_PlantId_StartedAt_Id",
                table: "Alarms",
                columns: new[] { "PlantId", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_RuleId",
                table: "Alarms",
                column: "RuleId",
                unique: true,
                filter: "\"State\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmEvents");

            migrationBuilder.DropTable(
                name: "RuleRuntimeStates");

            migrationBuilder.DropTable(
                name: "Alarms");

            migrationBuilder.DropTable(
                name: "AlarmRules");
        }
    }
}
