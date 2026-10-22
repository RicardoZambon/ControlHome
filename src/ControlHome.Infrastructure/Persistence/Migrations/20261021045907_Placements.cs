using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlHome.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Placements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Placements",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    XCm = table.Column<double>(type: "float", nullable: false),
                    YCm = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Placements", x => x.Id);
                    table.CheckConstraint("CK_Placements_Target", "([EndpointId] IS NOT NULL AND [DeviceId] IS NULL) OR ([EndpointId] IS NULL AND [DeviceId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Placements_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "ch",
                        principalTable: "Devices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Placements_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalSchema: "ch",
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Placements_Floors_FloorId",
                        column: x => x.FloorId,
                        principalSchema: "ch",
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Placements_DeviceId",
                schema: "ch",
                table: "Placements",
                column: "DeviceId",
                unique: true,
                filter: "[DeviceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Placements_EndpointId",
                schema: "ch",
                table: "Placements",
                column: "EndpointId",
                unique: true,
                filter: "[EndpointId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Placements_FloorId",
                schema: "ch",
                table: "Placements",
                column: "FloorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Placements",
                schema: "ch");
        }
    }
}
