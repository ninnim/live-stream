using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Studio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_branding",
                columns: table => new
                {
                    live_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logo_data_uri = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: true),
                    logo_position = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    logo_opacity_percent = table.Column<int>(type: "integer", nullable: false),
                    accent_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    show_logo = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_branding", x => x.live_session_id);
                    table.ForeignKey(
                        name: "fk_session_branding_live_sessions_live_session_id",
                        column: x => x.live_session_id,
                        principalTable: "live_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_scenes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    live_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    layout = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    primary_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    secondary_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lower_third_title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lower_third_subtitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_scenes", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_scenes_live_sessions_live_session_id",
                        column: x => x.live_session_id,
                        principalTable: "live_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_scenes_live_session_id_position",
                table: "session_scenes",
                columns: new[] { "live_session_id", "position" });

            // Branding is required, and only newly created sessions build their own. Without this,
            // every session that already exists loads with a null Branding and the studio throws on
            // open — a migration that deploys cleanly and breaks every existing show.
            //
            // The literals repeat SessionBranding's defaults rather than referencing them: a
            // migration describes the database at a point in time, and must not change meaning
            // later because a default was edited.
            migrationBuilder.Sql(
                """
                INSERT INTO session_branding
                    (live_session_id, logo_position, logo_opacity_percent, accent_color, show_logo, updated_at)
                SELECT id, 'TopRight', 80, '#0EA5E9', false, now()
                FROM live_sessions
                ON CONFLICT (live_session_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_branding");

            migrationBuilder.DropTable(
                name: "session_scenes");
        }
    }
}
