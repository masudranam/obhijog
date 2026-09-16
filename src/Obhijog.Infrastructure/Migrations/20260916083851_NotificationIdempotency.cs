using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obhijog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class NotificationIdempotency : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "reopen_count",
            table: "notifications",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "ix_notifications_complaint_id_recipient_id_type_reopen_count",
            table: "notifications",
            columns: new[] { "complaint_id", "recipient_id", "type", "reopen_count" },
            unique: true,
            filter: "type IN ('SlaWarning', 'SlaBreached', 'SlaEscalatedLevel2')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_notifications_complaint_id_recipient_id_type_reopen_count",
            table: "notifications");

        migrationBuilder.DropColumn(
            name: "reopen_count",
            table: "notifications");
    }
}
