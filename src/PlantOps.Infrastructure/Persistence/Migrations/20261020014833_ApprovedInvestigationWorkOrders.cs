using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedInvestigationWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    AiRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DraftOrigin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvestigationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssetVersion = table.Column<long>(type: "bigint", nullable: false),
                    AlarmVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovalCorrelationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExecutionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionProposals", x => x.Id);
                    table.UniqueConstraint("AK_ActionProposals_PlantId_AssetId_Id", x => new { x.PlantId, x.AssetId, x.Id });
                    table.CheckConstraint("CK_ActionProposal_State", "\"Status\" IN ('Pending','Rejected','Executed','Expired','Invalidated') AND \"ActionType\" = 'CreateWorkOrder' AND \"DraftOrigin\" = 'InvestigationTemplate' AND \"Version\" > 0 AND \"ExpiresAt\" > \"CreatedAt\" AND ((\"Status\" = 'Executed' AND \"ResultEntityId\" IS NOT NULL AND \"ExecutedAt\" IS NOT NULL AND \"ExecutionKey\" IS NOT NULL) OR (\"Status\" <> 'Executed' AND \"ResultEntityId\" IS NULL AND \"ExecutedAt\" IS NULL AND \"ExecutionKey\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_ActionProposals_AiRuns_AiRunId",
                        column: x => x.AiRunId,
                        principalTable: "AiRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActionProposals_Alarms_PlantId_AssetId_AlarmId",
                        columns: x => new { x.PlantId, x.AssetId, x.AlarmId },
                        principalTable: "Alarms",
                        principalColumns: new[] { "PlantId", "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActionProposals_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActionProposals_Assets_PlantId_AssetId",
                        columns: x => new { x.PlantId, x.AssetId },
                        principalTable: "Assets",
                        principalColumns: new[] { "PlantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PlantId_AssetId_AiProposalId",
                table: "WorkOrders",
                columns: new[] { "PlantId", "AssetId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionProposals_AiRunId",
                table: "ActionProposals",
                column: "AiRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionProposals_OwnerUserId_AiRunId_CreatedAt",
                table: "ActionProposals",
                columns: new[] { "OwnerUserId", "AiRunId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionProposals_PlantId_AssetId_AlarmId",
                table: "ActionProposals",
                columns: new[] { "PlantId", "AssetId", "AlarmId" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrders_ActionProposals_PlantId_AssetId_AiProposalId",
                table: "WorkOrders",
                columns: new[] { "PlantId", "AssetId", "AiProposalId" },
                principalTable: "ActionProposals",
                principalColumns: new[] { "PlantId", "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_ActionProposals_PlantId_AssetId_AiProposalId",
                table: "WorkOrders");

            migrationBuilder.DropTable(
                name: "ActionProposals");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_PlantId_AssetId_AiProposalId",
                table: "WorkOrders");
        }
    }
}
