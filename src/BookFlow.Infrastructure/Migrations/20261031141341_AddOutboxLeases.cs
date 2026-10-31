using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeaseId",
                table: "Outbox",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseUntilUtcTicks",
                table: "Outbox",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtcTicks",
                table: "Outbox");
        }
    }
}
