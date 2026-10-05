using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Alarms_PlantId_AssetId_Id",
                table: "Alarms",
                columns: new[] { "PlantId", "AssetId", "Id" });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlarmId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssignedToUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.CheckConstraint("CK_WorkOrder_State", "\"Status\" IN ('Draft','Open','InProgress','Completed','Closed','Cancelled') AND \"Priority\" IN ('Low','Medium','High') AND (\"Status\" NOT IN ('Completed','Closed') OR length(trim(\"Resolution\")) > 0 AND \"Resolution\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkOrders_Alarms_PlantId_AssetId_AlarmId",
                        columns: x => new { x.PlantId, x.AssetId, x.AlarmId },
                        principalTable: "Alarms",
                        principalColumns: new[] { "PlantId", "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Assets_PlantId_AssetId",
                        columns: x => new { x.PlantId, x.AssetId },
                        principalTable: "Assets",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderNotes_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderNotes_WorkOrderId_OccurredAt_Id",
                table: "WorkOrderNotes",
                columns: new[] { "WorkOrderId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_AiProposalId",
                table: "WorkOrders",
                column: "AiProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_AssetId_Status_CreatedAt_Id",
                table: "WorkOrders",
                columns: new[] { "AssetId", "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PlantId_AssetId_AlarmId",
                table: "WorkOrders",
                columns: new[] { "PlantId", "AssetId", "AlarmId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PlantId_Number",
                table: "WorkOrders",
                columns: new[] { "PlantId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkOrderNotes");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Alarms_PlantId_AssetId_Id",
                table: "Alarms");
        }
    }
}
