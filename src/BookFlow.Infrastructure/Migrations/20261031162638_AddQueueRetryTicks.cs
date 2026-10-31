using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueRetryTicks : Migration
    {
        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks",
                table: "Outbox");

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcTicks",
                table: "Outbox",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcTicks",
                table: "NotificationDeliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcTicks",
                table: "LocalDeliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                @"UPDATE [Outbox]
                  SET [NextAttemptAtUtcTicks] =
                      DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                          SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                      + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                  WHERE [NextAttemptAt] IS NOT NULL;

                  UPDATE [LocalDeliveries]
                  SET [NextAttemptAtUtcTicks] =
                      DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                          SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                      + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                  WHERE [NextAttemptAt] IS NOT NULL;

                  UPDATE [NotificationDeliveries]
                  SET [NextAttemptAtUtcTicks] =
                      DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                          SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                      + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                  WHERE [NextAttemptAt] IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Id",
                table: "Outbox",
                columns: new[] { "Published", "DeadLettered", "LeaseUntilUtcTicks", "NextAttemptAtUtcTicks", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries",
                columns: new[] { "Sent", "DeadLettered", "NextAttemptAtUtcTicks", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalDeliveries_Consumer_Completed_DeadLettered_NextAttemptAtUtcTicks_EventId",
                table: "LocalDeliveries",
                columns: new[] { "Consumer", "Completed", "DeadLettered", "NextAttemptAtUtcTicks", "EventId" });
        }

        /// <inheritdoc />
        /// <param name="migrationBuilder">The builder used to apply the schema operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Id",
                table: "Outbox");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key",
                table: "NotificationDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_LocalDeliveries_Consumer_Completed_DeadLettered_NextAttemptAtUtcTicks_EventId",
                table: "LocalDeliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtcTicks",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtcTicks",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtcTicks",
                table: "LocalDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks",
                table: "Outbox",
                columns: new[] { "Published", "DeadLettered", "LeaseUntilUtcTicks" });
        }
    }
}
