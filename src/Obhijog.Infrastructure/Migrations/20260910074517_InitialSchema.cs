using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Obhijog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateSequence(
            name: "complaint_reference_seq");

        migrationBuilder.CreateTable(
            name: "AspNetRoles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                concurrency_stamp = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_roles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "departments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_departments", x => x.id);
                table.CheckConstraint("ck_department_code_uppercase", "code = upper(code)");
            });

        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                subject = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_notifications", x => x.id);
                table.CheckConstraint("ck_notification_attempts_non_negative", "attempts >= 0");
            });

        migrationBuilder.CreateTable(
            name: "AspNetRoleClaims",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_type = table.Column<string>(type: "text", nullable: true),
                claim_value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_role_claims", x => x.id);
                table.ForeignKey(
                    name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                    column: x => x.role_id,
                    principalTable: "AspNetRoles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                full_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                phone = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                department_id = table.Column<Guid>(type: "uuid", nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: true),
                security_stamp = table.Column<string>(type: "text", nullable: true),
                concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                phone_number = table.Column<string>(type: "text", nullable: true),
                phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                access_failed_count = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_users", x => x.id);
                table.CheckConstraint("ck_user_department_matches_role", "(role = 'Citizen' AND department_id IS NULL) OR (role <> 'Citizen' AND department_id IS NOT NULL)");
                table.ForeignKey(
                    name: "fk_asp_net_users_departments_department_id",
                    column: x => x.department_id,
                    principalTable: "departments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "complaint_categories",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                department_id = table.Column<Guid>(type: "uuid", nullable: false),
                sla_hours = table.Column<int>(type: "integer", nullable: false),
                default_priority = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_complaint_categories", x => x.id);
                table.CheckConstraint("ck_complaint_category_sla_hours_range", "sla_hours > 0 AND sla_hours <= 8760");
                table.ForeignKey(
                    name: "fk_complaint_categories_departments_department_id",
                    column: x => x.department_id,
                    principalTable: "departments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_type = table.Column<string>(type: "text", nullable: true),
                claim_value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_user_claims", x => x.id);
                table.ForeignKey(
                    name: "fk_asp_net_user_claims_asp_net_users_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                login_provider = table.Column<string>(type: "text", nullable: false),
                provider_key = table.Column<string>(type: "text", nullable: false),
                provider_display_name = table.Column<string>(type: "text", nullable: true),
                user_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                table.ForeignKey(
                    name: "fk_asp_net_user_logins_asp_net_users_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_user_roles", x => new { x.user_id, x.role_id });
                table.ForeignKey(
                    name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                    column: x => x.role_id,
                    principalTable: "AspNetRoles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_asp_net_user_roles_asp_net_users_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_provider = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                table.ForeignKey(
                    name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "complaints",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reference_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                citizen_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                department_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                priority = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                address_text = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                assigned_staff_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sla_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sla_warned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                sla_breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                escalation_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                reopen_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_complaints", x => x.id);
                table.CheckConstraint("ck_complaint_escalation_level_range", "escalation_level >= 0 AND escalation_level <= 2");
                table.CheckConstraint("ck_complaint_latitude_range", "latitude >= -90 AND latitude <= 90");
                table.CheckConstraint("ck_complaint_longitude_range", "longitude >= -180 AND longitude <= 180");
                table.CheckConstraint("ck_complaint_rejection_reason_required", "status <> 'Rejected' OR rejection_reason IS NOT NULL");
                table.CheckConstraint("ck_complaint_reopen_count_non_negative", "reopen_count >= 0");
                table.CheckConstraint("ck_complaint_resolution_note_required", "status NOT IN ('Resolved', 'Closed') OR resolution_note IS NOT NULL");
                table.ForeignKey(
                    name: "fk_complaints_complaint_categories_category_id",
                    column: x => x.category_id,
                    principalTable: "complaint_categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_complaints_departments_department_id",
                    column: x => x.department_id,
                    principalTable: "departments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "complaint_attachments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                blob_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                original_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                content_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_complaint_attachments", x => x.id);
                table.CheckConstraint("ck_complaint_attachment_size_positive", "size_bytes > 0");
                table.ForeignKey(
                    name: "fk_complaint_attachments_complaints_complaint_id",
                    column: x => x.complaint_id,
                    principalTable: "complaints",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "complaint_comments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                author_id = table.Column<Guid>(type: "uuid", nullable: false),
                body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                is_internal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_complaint_comments", x => x.id);
                table.ForeignKey(
                    name: "fk_complaint_comments_complaints_complaint_id",
                    column: x => x.complaint_id,
                    principalTable: "complaints",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "complaint_status_histories",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                from_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                to_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                changed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                is_system = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_complaint_status_histories", x => x.id);
                table.CheckConstraint("ck_complaint_status_history_actor", "(is_system AND changed_by_id IS NULL) OR (NOT is_system AND changed_by_id IS NOT NULL)");
                table.ForeignKey(
                    name: "fk_complaint_status_histories_complaints_complaint_id",
                    column: x => x.complaint_id,
                    principalTable: "complaints",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "escalation_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                reopen_count = table.Column<int>(type: "integer", nullable: false),
                level = table.Column<short>(type: "smallint", nullable: false),
                raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                notified_user_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_escalation_events", x => x.id);
                table.CheckConstraint("ck_escalation_event_level_range", "level IN (1, 2)");
                table.ForeignKey(
                    name: "fk_escalation_events_complaints_complaint_id",
                    column: x => x.complaint_id,
                    principalTable: "complaints",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_asp_net_role_claims_role_id",
            table: "AspNetRoleClaims",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "normalized_name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_asp_net_user_claims_user_id",
            table: "AspNetUserClaims",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_asp_net_user_logins_user_id",
            table: "AspNetUserLogins",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_asp_net_user_roles_role_id",
            table: "AspNetUserRoles",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "normalized_email");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "normalized_user_name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_asp_net_users_department_id_role",
            table: "AspNetUsers",
            columns: new[] { "department_id", "role" },
            filter: "is_active");

        migrationBuilder.CreateIndex(
            name: "ix_complaint_attachments_blob_name",
            table: "complaint_attachments",
            column: "blob_name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_complaint_attachments_complaint_id_uploaded_at",
            table: "complaint_attachments",
            columns: new[] { "complaint_id", "uploaded_at" });

        migrationBuilder.CreateIndex(
            name: "ix_complaint_categories_department_id",
            table: "complaint_categories",
            column: "department_id");

        migrationBuilder.CreateIndex(
            name: "ix_complaint_categories_is_active_department_id",
            table: "complaint_categories",
            columns: new[] { "is_active", "department_id" });

        migrationBuilder.CreateIndex(
            name: "ix_complaint_categories_name",
            table: "complaint_categories",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_complaint_comments_complaint_id_created_at",
            table: "complaint_comments",
            columns: new[] { "complaint_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_complaint_status_histories_complaint_id_changed_at",
            table: "complaint_status_histories",
            columns: new[] { "complaint_id", "changed_at" });

        migrationBuilder.CreateIndex(
            name: "ix_complaints_assigned_staff_id_status",
            table: "complaints",
            columns: new[] { "assigned_staff_id", "status" },
            filter: "assigned_staff_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_complaints_category_id",
            table: "complaints",
            column: "category_id");

        migrationBuilder.CreateIndex(
            name: "ix_complaints_citizen_id_created_at",
            table: "complaints",
            columns: new[] { "citizen_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_complaints_department_id_status_sla_due_at",
            table: "complaints",
            columns: new[] { "department_id", "status", "sla_due_at" });

        migrationBuilder.CreateIndex(
            name: "ix_complaints_reference_number",
            table: "complaints",
            column: "reference_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_complaints_status_sla_due_at",
            table: "complaints",
            columns: new[] { "status", "sla_due_at" },
            filter: "status IN ('New', 'Assigned', 'InProgress')");

        migrationBuilder.CreateIndex(
            name: "ix_departments_code",
            table: "departments",
            column: "code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_departments_name",
            table: "departments",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_escalation_events_complaint_id_reopen_count_level",
            table: "escalation_events",
            columns: new[] { "complaint_id", "reopen_count", "level" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_notifications_recipient_id_created_at",
            table: "notifications",
            columns: new[] { "recipient_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_notifications_sent_at",
            table: "notifications",
            column: "sent_at",
            filter: "sent_at IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "complaint_attachments");

        migrationBuilder.DropTable(
            name: "complaint_comments");

        migrationBuilder.DropTable(
            name: "complaint_status_histories");

        migrationBuilder.DropTable(
            name: "escalation_events");

        migrationBuilder.DropTable(
            name: "notifications");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropTable(
            name: "complaints");

        migrationBuilder.DropTable(
            name: "complaint_categories");

        migrationBuilder.DropTable(
            name: "departments");

        migrationBuilder.DropSequence(
            name: "complaint_reference_seq");
    }
}
