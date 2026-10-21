using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ControlledOpcUaSourceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataSourceHealth",
                columns: table => new
                {
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastKeepAliveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReconnectCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSourceHealth", x => x.DataSourceId);
                    table.ForeignKey(
                        name: "FK_DataSourceHealth_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataSourceHealth");
        }
    }
}
