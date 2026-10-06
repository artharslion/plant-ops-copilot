using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewIngestionAndOutboxSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DroppedCount",
                table: "IngestionGaps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPossible",
                table: "IngestionGaps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveredAt",
                table: "IngestionGaps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_DispatchedAt_Id",
                table: "OutboxEvents",
                columns: new[] { "DispatchedAt", "Id" },
                filter: "\"DispatchedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxEvents_DispatchedAt_Id",
                table: "OutboxEvents");

            migrationBuilder.DropColumn(
                name: "DroppedCount",
                table: "IngestionGaps");

            migrationBuilder.DropColumn(
                name: "IsPossible",
                table: "IngestionGaps");

            migrationBuilder.DropColumn(
                name: "RecoveredAt",
                table: "IngestionGaps");
        }
    }
}
