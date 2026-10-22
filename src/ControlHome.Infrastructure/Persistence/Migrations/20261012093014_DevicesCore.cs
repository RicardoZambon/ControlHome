using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlHome.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicesCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SafetyPinHash",
                schema: "auth",
                table: "Users",
                type: "varchar(512)",
                unicode: false,
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Integrations",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConfigJson = table.Column<string>(type: "json", nullable: false),
                    SecretsProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ParentDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeviceClass = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Firmware = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConnectionsJson = table.Column<string>(type: "json", nullable: false),
                    Availability = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsOrphaned = table.Column<bool>(type: "bit", nullable: false),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Devices_ParentDeviceId",
                        column: x => x.ParentDeviceId,
                        principalSchema: "ch",
                        principalTable: "Devices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Devices_Integrations_IntegrationId",
                        column: x => x.IntegrationId,
                        principalSchema: "ch",
                        principalTable: "Integrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Devices_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalSchema: "ch",
                        principalTable: "Rooms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Endpoints",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    Favorite = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Endpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Endpoints_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "ch",
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Endpoints_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalSchema: "ch",
                        principalTable: "Rooms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EndpointCapabilities",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapabilityId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CapabilityVersion = table.Column<int>(type: "int", nullable: false),
                    ConstraintsJson = table.Column<string>(type: "json", nullable: true),
                    GenericDefinitionJson = table.Column<string>(type: "json", nullable: true),
                    Retired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointCapabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndpointCapabilities_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalSchema: "ch",
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAttributes",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EndpointCapabilityId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceAttributes_EndpointCapabilities_EndpointCapabilityId",
                        column: x => x.EndpointCapabilityId,
                        principalSchema: "ch",
                        principalTable: "EndpointCapabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttributeState",
                schema: "ch",
                columns: table => new
                {
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    Num = table.Column<double>(type: "float", nullable: true),
                    Str = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Json = table.Column<string>(type: "json", nullable: true),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeState", x => x.AttributeId);
                    table.ForeignKey(
                        name: "FK_AttributeState_DeviceAttributes_AttributeId",
                        column: x => x.AttributeId,
                        principalSchema: "ch",
                        principalTable: "DeviceAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAttributes_EndpointCapabilityId_Key",
                schema: "ch",
                table: "DeviceAttributes",
                columns: new[] { "EndpointCapabilityId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IntegrationId_ExternalId",
                schema: "ch",
                table: "Devices",
                columns: new[] { "IntegrationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ParentDeviceId",
                schema: "ch",
                table: "Devices",
                column: "ParentDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_RoomId",
                schema: "ch",
                table: "Devices",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCapabilities_CapabilityId",
                schema: "ch",
                table: "EndpointCapabilities",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCapabilities_EndpointId_CapabilityId",
                schema: "ch",
                table: "EndpointCapabilities",
                columns: new[] { "EndpointId", "CapabilityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_DeviceId_Key",
                schema: "ch",
                table: "Endpoints",
                columns: new[] { "DeviceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_RoomId",
                schema: "ch",
                table: "Endpoints",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_ConnectorKey",
                schema: "ch",
                table: "Integrations",
                column: "ConnectorKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttributeState",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "DeviceAttributes",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "EndpointCapabilities",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "Endpoints",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "Devices",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "Integrations",
                schema: "ch");

            migrationBuilder.DropColumn(
                name: "SafetyPinHash",
                schema: "auth",
                table: "Users");
        }
    }
}
