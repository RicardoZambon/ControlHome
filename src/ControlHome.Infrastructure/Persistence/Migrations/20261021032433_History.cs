using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlHome.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class History : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceEvent",
                schema: "ch",
                columns: table => new
                {
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    TsUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "json", nullable: true)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "EnergyHourly",
                schema: "ch",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HourStartUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Wh = table.Column<double>(type: "float", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Estimated = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergyHourly", x => new { x.EndpointId, x.HourStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "EnergySourceSelection",
                schema: "ch",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    AttributeId = table.Column<int>(type: "int", nullable: true),
                    IsUserOverride = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergySourceSelection", x => x.EndpointId);
                });

            migrationBuilder.CreateTable(
                name: "RetentionSettings",
                schema: "ch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StateLogDays = table.Column<int>(type: "int", nullable: false),
                    ShortTermStatsDays = table.Column<int>(type: "int", nullable: false),
                    DeviceEventDays = table.Column<int>(type: "int", nullable: false),
                    AuditLogDays = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionSettings", x => x.Id);
                    table.CheckConstraint("CK_RetentionSettings_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "RollupProgress",
                schema: "ch",
                columns: table => new
                {
                    Name = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    WatermarkUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RollupProgress", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "StateLog",
                schema: "ch",
                columns: table => new
                {
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    TsUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Num = table.Column<double>(type: "float", nullable: true),
                    Str = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "StatHourly",
                schema: "ch",
                columns: table => new
                {
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    BucketStartUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Mean = table.Column<double>(type: "float", nullable: true),
                    Min = table.Column<double>(type: "float", nullable: true),
                    Max = table.Column<double>(type: "float", nullable: true),
                    Sum = table.Column<double>(type: "float", nullable: true),
                    LastCounter = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatHourly", x => new { x.AttributeId, x.BucketStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "StatShortTerm",
                schema: "ch",
                columns: table => new
                {
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    BucketStartUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Mean = table.Column<double>(type: "float", nullable: true),
                    Min = table.Column<double>(type: "float", nullable: true),
                    Max = table.Column<double>(type: "float", nullable: true),
                    Sum = table.Column<double>(type: "float", nullable: true),
                    LastCounter = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatShortTerm", x => new { x.AttributeId, x.BucketStartUtc });
                });

            migrationBuilder.CreateIndex(
                name: "CIX_DeviceEvent",
                schema: "ch",
                table: "DeviceEvent",
                columns: new[] { "TsUtc", "AttributeId" })
                .Annotation("SqlServer:Clustered", true)
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEvent_AttributeId_TsUtc",
                schema: "ch",
                table: "DeviceEvent",
                columns: new[] { "AttributeId", "TsUtc" })
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page)
                .Annotation("SqlServer:Include", new[] { "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_EnergyHourly_HourStartUtc",
                schema: "ch",
                table: "EnergyHourly",
                column: "HourStartUtc")
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page);

            migrationBuilder.CreateIndex(
                name: "CIX_StateLog",
                schema: "ch",
                table: "StateLog",
                columns: new[] { "TsUtc", "AttributeId" })
                .Annotation("SqlServer:Clustered", true)
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page);

            migrationBuilder.CreateIndex(
                name: "IX_StateLog_AttributeId_TsUtc",
                schema: "ch",
                table: "StateLog",
                columns: new[] { "AttributeId", "TsUtc" })
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page)
                .Annotation("SqlServer:Include", new[] { "Num", "Str" });

            migrationBuilder.CreateIndex(
                name: "IX_StatShortTerm_BucketStartUtc",
                schema: "ch",
                table: "StatShortTerm",
                column: "BucketStartUtc")
                .Annotation("SqlServer:DataCompression", DataCompressionType.Page);

            // Hand-written (EF cannot express these): PAGE compression of the primary keys (the tables' clustered
            // indexes), and the table types the write-behind appends history with (table-valued parameters).
            migrationBuilder.Sql("ALTER TABLE [ch].[StatShortTerm] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql("ALTER TABLE [ch].[StatHourly] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql("ALTER TABLE [ch].[EnergyHourly] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql("""
                CREATE TYPE [ch].[StateLogRow] AS TABLE (
                    AttributeId int NOT NULL,
                    TsUtc datetime2(3) NOT NULL,
                    Num float NULL,
                    Str nvarchar(255) NULL);
                """);
            migrationBuilder.Sql("""
                CREATE TYPE [ch].[DeviceEventRow] AS TABLE (
                    AttributeId int NOT NULL,
                    TsUtc datetime2(3) NOT NULL,
                    EventType nvarchar(64) NOT NULL,
                    Payload nvarchar(max) NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TYPE [ch].[DeviceEventRow];");
            migrationBuilder.Sql("DROP TYPE [ch].[StateLogRow];");

            migrationBuilder.DropTable(
                name: "DeviceEvent",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "EnergyHourly",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "EnergySourceSelection",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "RetentionSettings",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "RollupProgress",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "StateLog",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "StatHourly",
                schema: "ch");

            migrationBuilder.DropTable(
                name: "StatShortTerm",
                schema: "ch");
        }
    }
}
