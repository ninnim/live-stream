using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4MultiDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    live_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    media_path_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pairing_code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pairing_code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    device_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    device_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_program = table.Column<bool>(type: "boolean", nullable: false),
                    ingest_connected = table.Column<bool>(type: "boolean", nullable: false),
                    bitrate_kbps = table.Column<int>(type: "integer", nullable: true),
                    bytes_received = table.Column<long>(type: "bigint", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_sources_live_sessions_live_session_id",
                        column: x => x.live_session_id,
                        principalTable: "live_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_source_events_session_sources_session_source_id",
                        column: x => x.session_source_id,
                        principalTable: "session_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_sources_device_token_hash",
                table: "session_sources",
                column: "device_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_sources_media_path",
                table: "session_sources",
                column: "media_path_name");

            migrationBuilder.CreateIndex(
                name: "ix_session_sources_pairing_code_hash",
                table: "session_sources",
                column: "pairing_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_sources_session_created",
                table: "session_sources",
                columns: new[] { "live_session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_sources_status",
                table: "session_sources",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_source_events_source_created",
                table: "source_events",
                columns: new[] { "session_source_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_events");

            migrationBuilder.DropTable(
                name: "session_sources");
        }
    }
}
