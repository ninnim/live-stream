using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Distribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    access_token_cipher = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    refresh_token_cipher = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    linked_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stream_destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    live_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    credential_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ingest_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    stream_key_cipher = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    provider_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolved_ingest_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resolved_stream_key_cipher = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    external_broadcast_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    watch_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    bytes_sent = table.Column<long>(type: "bigint", nullable: false),
                    state_entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stream_destinations", x => x.id);
                    table.ForeignKey(
                        name: "fk_stream_destinations_live_sessions_live_session_id",
                        column: x => x.live_session_id,
                        principalTable: "live_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stream_destinations_provider_accounts_provider_account_id",
                        column: x => x.provider_account_id,
                        principalTable: "provider_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "destination_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_destination_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_destination_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_destination_events_stream_destinations_stream_destination_id",
                        column: x => x.stream_destination_id,
                        principalTable: "stream_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_destination_events_destination_created",
                table: "destination_events",
                columns: new[] { "stream_destination_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_accounts_workspace_provider_external",
                table: "provider_accounts",
                columns: new[] { "workspace_id", "provider", "external_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stream_destinations_provider_account_id",
                table: "stream_destinations",
                column: "provider_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_stream_destinations_session_created",
                table: "stream_destinations",
                columns: new[] { "live_session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_destinations_status",
                table: "stream_destinations",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "destination_events");

            migrationBuilder.DropTable(
                name: "stream_destinations");

            migrationBuilder.DropTable(
                name: "provider_accounts");
        }
    }
}
