using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePublisherAndOutboxConstraints : Migration
    {
        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LeaseId",
                table: "Outbox",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_TenantId_Role",
                table: "Participants",
                columns: new[] { "TenantId", "Role" },
                unique: true,
                filter: "[Role] = 'Publisher'");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks",
                table: "Outbox",
                columns: new[] { "Published", "DeadLettered", "LeaseUntilUtcTicks" });
        }

        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_TenantId_Role",
                table: "Participants");

            migrationBuilder.DropIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks",
                table: "Outbox");

            migrationBuilder.AlterColumn<string>(
                name: "LeaseId",
                table: "Outbox",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(36)",
                oldMaxLength: 36,
                oldNullable: true);
        }
    }
}
