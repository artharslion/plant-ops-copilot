using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlantOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizedDocumentChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Section = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    StartLine = table.Column<int>(type: "integer", nullable: false),
                    EndLine = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParserVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunks", x => x.Id);
                    table.CheckConstraint("CK_DocumentChunk", "\"Ordinal\" >= 0 AND \"Ordinal\" < 256 AND \"StartLine\" > 0 AND \"EndLine\" >= \"StartLine\"");
                    table.ForeignKey(
                        name: "FK_DocumentChunks_DocumentVersions_DocumentId_VersionId",
                        columns: x => new { x.DocumentId, x.VersionId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "DocumentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_DocumentId_VersionId",
                table: "DocumentChunks",
                columns: new[] { "DocumentId", "VersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_VersionId_Ordinal",
                table: "DocumentChunks",
                columns: new[] { "VersionId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentChunks");
        }
    }
}
