using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlHome.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FloorsRoomsFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Floors",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    WidthCm = table.Column<int>(type: "int", nullable: false),
                    HeightCm = table.Column<int>(type: "int", nullable: false),
                    GridCm = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Background = table.Column<string>(type: "json", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Floors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    WidthPx = table.Column<int>(type: "int", nullable: false),
                    HeightPx = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rooms",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Icon = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ColorToken = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Polygon = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rooms_Floors_FloorId",
                        column: x => x.FloorId,
                        principalSchema: "ch",
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Floors_Level",
                schema: "ch",
                table: "Floors",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_FloorId_SortOrder",
                schema: "ch",
                table: "Rooms",
                columns: new[] { "FloorId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_Purpose",
                schema: "ch",
                table: "StoredFiles",
                column: "Purpose");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Rooms",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "StoredFiles",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "Floors",
                schema: "ch");
        }
    }
}
