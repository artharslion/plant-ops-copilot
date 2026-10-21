using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedInvestigationNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionProposal_State",
                table: "ActionProposals");

            migrationBuilder.AddColumn<Guid>(
                name: "AiProposalId",
                table: "WorkOrderNotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetWorkOrderId",
                table: "ActionProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TargetWorkOrderVersion",
                table: "ActionProposals",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_WorkOrders_PlantId_AssetId_Id",
                table: "WorkOrders",
                columns: new[] { "PlantId", "AssetId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderNotes_AiProposalId",
                table: "WorkOrderNotes",
                column: "AiProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActionProposals_PlantId_AssetId_TargetWorkOrderId",
                table: "ActionProposals",
                columns: new[] { "PlantId", "AssetId", "TargetWorkOrderId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionProposal_State",
                table: "ActionProposals",
                sql: "\"Status\" IN ('Pending','Rejected','Executed','Expired','Invalidated') AND ((\"ActionType\" = 'CreateWorkOrder' AND \"TargetWorkOrderId\" IS NULL AND \"TargetWorkOrderVersion\" IS NULL) OR (\"ActionType\" = 'AppendWorkOrderNote' AND \"TargetWorkOrderId\" IS NOT NULL AND \"TargetWorkOrderVersion\" > 0 AND \"TargetWorkOrderVersion\" IS NOT NULL)) AND \"DraftOrigin\" = 'InvestigationTemplate' AND \"Version\" > 0 AND \"ExpiresAt\" > \"CreatedAt\" AND ((\"Status\" = 'Executed' AND \"ResultEntityId\" IS NOT NULL AND \"ExecutedAt\" IS NOT NULL AND \"ExecutionKey\" IS NOT NULL) OR (\"Status\" <> 'Executed' AND \"ResultEntityId\" IS NULL AND \"ExecutedAt\" IS NULL AND \"ExecutionKey\" IS NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_ActionProposals_WorkOrders_PlantId_AssetId_TargetWorkOrderId",
                table: "ActionProposals",
                columns: new[] { "PlantId", "AssetId", "TargetWorkOrderId" },
                principalTable: "WorkOrders",
                principalColumns: new[] { "PlantId", "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrderNotes_ActionProposals_AiProposalId",
                table: "WorkOrderNotes",
                column: "AiProposalId",
                principalTable: "ActionProposals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActionProposals_WorkOrders_PlantId_AssetId_TargetWorkOrderId",
                table: "ActionProposals");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrderNotes_ActionProposals_AiProposalId",
                table: "WorkOrderNotes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_WorkOrders_PlantId_AssetId_Id",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrderNotes_AiProposalId",
                table: "WorkOrderNotes");

            migrationBuilder.DropIndex(
                name: "IX_ActionProposals_PlantId_AssetId_TargetWorkOrderId",
                table: "ActionProposals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionProposal_State",
                table: "ActionProposals");

            migrationBuilder.DropColumn(
                name: "AiProposalId",
                table: "WorkOrderNotes");

            migrationBuilder.DropColumn(
                name: "TargetWorkOrderId",
                table: "ActionProposals");

            migrationBuilder.DropColumn(
                name: "TargetWorkOrderVersion",
                table: "ActionProposals");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionProposal_State",
                table: "ActionProposals",
                sql: "\"Status\" IN ('Pending','Rejected','Executed','Expired','Invalidated') AND \"ActionType\" = 'CreateWorkOrder' AND \"DraftOrigin\" = 'InvestigationTemplate' AND \"Version\" > 0 AND \"ExpiresAt\" > \"CreatedAt\" AND ((\"Status\" = 'Executed' AND \"ResultEntityId\" IS NOT NULL AND \"ExecutedAt\" IS NOT NULL AND \"ExecutionKey\" IS NOT NULL) OR (\"Status\" <> 'Executed' AND \"ResultEntityId\" IS NULL AND \"ExecutedAt\" IS NULL AND \"ExecutionKey\" IS NULL))");
        }
    }
}
