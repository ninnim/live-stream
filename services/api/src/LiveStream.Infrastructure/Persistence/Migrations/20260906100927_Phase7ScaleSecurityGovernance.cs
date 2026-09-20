using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7ScaleSecurityGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "recordings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "recordings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "runtime_leases",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runtime_leases", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "user_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_identities", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_identities_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_limits",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    max_concurrent_sessions_override = table.Column<int>(type: "integer", nullable: true),
                    max_destinations_per_session_override = table.Column<int>(type: "integer", nullable: true),
                    max_sources_per_session_override = table.Column<int>(type: "integer", nullable: true),
                    recording_retention_days_override = table.Column<int>(type: "integer", nullable: true),
                    residency_region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_limits", x => x.workspace_id);
                    table.ForeignKey(
                        name: "fk_workspace_limits_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_sso_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    client_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    client_secret_ciphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    jit_provisioning = table.Column<bool>(type: "boolean", nullable: false),
                    default_role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_sso_connections", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_sso_connections_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_sso_domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_sso_domains", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_sso_domains_workspace_sso_connections_connection_~",
                        column: x => x.connection_id,
                        principalTable: "workspace_sso_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recordings_status_expires_at",
                table: "recordings",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_identities_issuer_subject",
                table: "user_identities",
                columns: new[] { "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_identities_user_id",
                table: "user_identities",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_sso_connections_workspace_id",
                table: "workspace_sso_connections",
                column: "workspace_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_sso_domains_connection_id",
                table: "workspace_sso_domains",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_sso_domains_domain",
                table: "workspace_sso_domains",
                column: "domain",
                unique: true);

            // Every workspace that already exists gets an explicit limits row on the Pro plan,
            // whose allowances are exactly the limits this platform has enforced since Phase 1.
            // Without this backfill, an upgrade would silently move existing tenants onto
            // whatever Governance:DefaultPlan happens to say — including Free, which allows one
            // concurrent session. Nobody's workspace should be restricted by a deployment.
            migrationBuilder.Sql("""
                INSERT INTO workspace_limits (workspace_id, plan, created_at, updated_at)
                SELECT id, 'Pro', now(), now() FROM workspaces
                ON CONFLICT (workspace_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_leases");

            migrationBuilder.DropTable(
                name: "user_identities");

            migrationBuilder.DropTable(
                name: "workspace_limits");

            migrationBuilder.DropTable(
                name: "workspace_sso_domains");

            migrationBuilder.DropTable(
                name: "workspace_sso_connections");

            migrationBuilder.DropIndex(
                name: "ix_recordings_status_expires_at",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "recordings");
        }
    }
}
