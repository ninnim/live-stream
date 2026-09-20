using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2DestinationByteBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bytes_sent_baseline",
                table: "stream_destinations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bytes_sent_baseline",
                table: "stream_destinations");
        }
    }
}
