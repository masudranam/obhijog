using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Obhijog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UserForeignKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_escalation_events_notified_user_id",
            table: "escalation_events",
            column: "notified_user_id");

        migrationBuilder.CreateIndex(
            name: "ix_complaint_status_histories_changed_by_id",
            table: "complaint_status_histories",
            column: "changed_by_id");

        migrationBuilder.CreateIndex(
            name: "ix_complaint_comments_author_id",
            table: "complaint_comments",
            column: "author_id");

        migrationBuilder.CreateIndex(
            name: "ix_complaint_attachments_uploaded_by_id",
            table: "complaint_attachments",
            column: "uploaded_by_id");

        migrationBuilder.AddForeignKey(
            name: "fk_complaint_attachments_asp_net_users_uploaded_by_id",
            table: "complaint_attachments",
            column: "uploaded_by_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_complaint_comments_asp_net_users_author_id",
            table: "complaint_comments",
            column: "author_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_complaint_status_histories_asp_net_users_changed_by_id",
            table: "complaint_status_histories",
            column: "changed_by_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_complaints_asp_net_users_assigned_staff_id",
            table: "complaints",
            column: "assigned_staff_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_complaints_asp_net_users_citizen_id",
            table: "complaints",
            column: "citizen_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_escalation_events_asp_net_users_notified_user_id",
            table: "escalation_events",
            column: "notified_user_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_notifications_asp_net_users_recipient_id",
            table: "notifications",
            column: "recipient_id",
            principalTable: "AspNetUsers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_complaint_attachments_asp_net_users_uploaded_by_id",
            table: "complaint_attachments");

        migrationBuilder.DropForeignKey(
            name: "fk_complaint_comments_asp_net_users_author_id",
            table: "complaint_comments");

        migrationBuilder.DropForeignKey(
            name: "fk_complaint_status_histories_asp_net_users_changed_by_id",
            table: "complaint_status_histories");

        migrationBuilder.DropForeignKey(
            name: "fk_complaints_asp_net_users_assigned_staff_id",
            table: "complaints");

        migrationBuilder.DropForeignKey(
            name: "fk_complaints_asp_net_users_citizen_id",
            table: "complaints");

        migrationBuilder.DropForeignKey(
            name: "fk_escalation_events_asp_net_users_notified_user_id",
            table: "escalation_events");

        migrationBuilder.DropForeignKey(
            name: "fk_notifications_asp_net_users_recipient_id",
            table: "notifications");

        migrationBuilder.DropIndex(
            name: "ix_escalation_events_notified_user_id",
            table: "escalation_events");

        migrationBuilder.DropIndex(
            name: "ix_complaint_status_histories_changed_by_id",
            table: "complaint_status_histories");

        migrationBuilder.DropIndex(
            name: "ix_complaint_comments_author_id",
            table: "complaint_comments");

        migrationBuilder.DropIndex(
            name: "ix_complaint_attachments_uploaded_by_id",
            table: "complaint_attachments");
    }
}
