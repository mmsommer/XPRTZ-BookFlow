using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeaseNotificationDelivery : Migration
    {
        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries");

            migrationBuilder.AddColumn<string>(
                name: "LeaseId",
                table: "NotificationDeliveries",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseUntilUtcTicks",
                table: "NotificationDeliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries",
                columns: new[] { "Sent", "DeadLettered", "LeaseUntilUtcTicks", "NextAttemptAtUtcTicks", "Key" });
        }

        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtcTicks",
                table: "NotificationDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries",
                columns: new[] { "Sent", "DeadLettered", "NextAttemptAtUtcTicks", "Key" });
        }
    }
}
