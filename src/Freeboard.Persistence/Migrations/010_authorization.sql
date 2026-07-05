-- Per-org authorization foundation: roles, permissions, and assignments as DATA, plus a minimal
-- persistent audit trail. Ids and FK columns use utf8mb4_bin to match Core's exact-byte id identity.
-- The migration runner replays a partially-failed migration (the version stays unrecorded on partial
-- failure), so every statement is idempotent/re-runnable: CREATE TABLE IF NOT EXISTS for the tables,
-- and INSERT ... ON DUPLICATE KEY UPDATE / INSERT IGNORE for every seed and backfill row.
--
-- Role scope is a PERSISTED invariant: authz_roles.scope (system|organisation) constrains which
-- assignment table a role may be written to. The organisation FK is ON DELETE RESTRICT (matching
-- 007/009): an org delete is guarded, and the org-delete path plus the GitOps importer prune the
-- assignment rows before deleting the organisation. The user/role FKs cascade/restrict per D8.

CREATE TABLE IF NOT EXISTS authz_roles (
    role_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    title VARCHAR(190) NOT NULL,
    description VARCHAR(512) NOT NULL,
    scope VARCHAR(16) NOT NULL,
    is_system TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (role_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS authz_permissions (
    permission_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    resource_type VARCHAR(32) NOT NULL,
    description VARCHAR(512) NOT NULL,
    is_system TINYINT(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (permission_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS authz_role_permissions (
    role_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    permission_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (role_key, permission_key),
    KEY ix_authz_role_permissions_permission (permission_key),
    CONSTRAINT fk_authz_role_permissions_role
        FOREIGN KEY (role_key) REFERENCES authz_roles (role_key) ON DELETE CASCADE,
    CONSTRAINT fk_authz_role_permissions_permission
        FOREIGN KEY (permission_key) REFERENCES authz_permissions (permission_key) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS authz_system_role_assignments (
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    role_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_id, role_key),
    KEY ix_authz_system_role_assignments_role (role_key),
    CONSTRAINT fk_authz_system_role_assignments_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
    CONSTRAINT fk_authz_system_role_assignments_role
        FOREIGN KEY (role_key) REFERENCES authz_roles (role_key) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS authz_organisation_role_assignments (
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    role_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_id, role_key, organisation_id),
    KEY ix_authz_org_role_assignments_role (role_key),
    KEY ix_authz_org_role_assignments_org (organisation_id),
    CONSTRAINT fk_authz_org_role_assignments_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
    CONSTRAINT fk_authz_org_role_assignments_role
        FOREIGN KEY (role_key) REFERENCES authz_roles (role_key) ON DELETE RESTRICT,
    CONSTRAINT fk_authz_org_role_assignments_org
        FOREIGN KEY (organisation_id) REFERENCES organisations (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Scalar actor/resource ids and NO strict FKs, so audit history survives user and organisation
-- deletes. The persisted set is the security-relevant privilege-and-exposure trail (D9).
CREATE TABLE IF NOT EXISTS authz_audit_events (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    event_type VARCHAR(64) NOT NULL,
    actor_user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    action VARCHAR(190) NULL,
    resource_type VARCHAR(64) NULL,
    resource_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    effect VARCHAR(16) NULL,
    reason VARCHAR(512) NULL,
    PRIMARY KEY (id),
    KEY ix_authz_audit_events_occurred_at (occurred_at),
    KEY ix_authz_audit_events_actor (actor_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Seed the four system roles with their persisted scope.
INSERT INTO authz_roles (role_key, title, description, scope, is_system, created_at, updated_at) VALUES
    ('super-admin', 'Super Admin', 'System-wide administrator (break-glass, permit-all).', 'system', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('org-owner', 'Organisation Owner', 'Full control of an organisation subtree, including role delegation.', 'organisation', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('compliance-manager', 'Compliance Manager', 'Read and write compliance scoping within an organisation subtree.', 'organisation', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('compliance-reader', 'Compliance Reader', 'Read-only access to an organisation subtree.', 'organisation', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE title = VALUES(title), description = VALUES(description), scope = VALUES(scope),
    is_system = VALUES(is_system), updated_at = VALUES(updated_at);

-- Seed the eight permission keys.
INSERT INTO authz_permissions (permission_key, resource_type, description, is_system) VALUES
    ('system.admin', 'system', 'System-wide permit-all.', 1),
    ('authz.assignment.write', 'organisation', 'Manage org-scoped role assignments.', 1),
    ('org.read', 'organisation', 'Read organisations.', 1),
    ('org.write', 'organisation', 'Create, update, and delete organisations.', 1),
    ('compliance.read', 'organisation', 'Read compliance scoping.', 1),
    ('compliance.scope.write', 'scope', 'Write standard-level scope dispositions.', 1),
    ('compliance.requirement-scope.write', 'requirement_scope', 'Write requirement-level scope dispositions.', 1),
    ('user.manage', 'user', 'Administer users and cross-user sessions.', 1)
ON DUPLICATE KEY UPDATE resource_type = VALUES(resource_type), description = VALUES(description),
    is_system = VALUES(is_system);

-- Seed the role-to-permission map. user.manage is held by NO seeded role: it is reachable only
-- through system.admin (super-admin), keeping user administration super-admin-only.
INSERT INTO authz_role_permissions (role_key, permission_key) VALUES
    ('super-admin', 'system.admin'),
    ('org-owner', 'org.read'),
    ('org-owner', 'org.write'),
    ('org-owner', 'compliance.read'),
    ('org-owner', 'compliance.scope.write'),
    ('org-owner', 'compliance.requirement-scope.write'),
    ('org-owner', 'authz.assignment.write'),
    ('compliance-manager', 'org.read'),
    ('compliance-manager', 'compliance.read'),
    ('compliance-manager', 'compliance.scope.write'),
    ('compliance-manager', 'compliance.requirement-scope.write'),
    ('compliance-reader', 'org.read'),
    ('compliance-reader', 'compliance.read')
ON DUPLICATE KEY UPDATE role_key = VALUES(role_key);

-- Backfill: every legacy global_role='admin' user becomes a super-admin, so existing installs stay
-- administrable once the super-admin authz assignment (not the legacy claim) is the sole system power.
INSERT IGNORE INTO authz_system_role_assignments (user_id, role_key, created_at, updated_at)
SELECT id, 'super-admin', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM users
WHERE global_role = 'admin';

-- Member backfill: every existing ENABLED non-admin user gets compliance-reader on the current ROOT
-- organisations (a root grant covers the whole tree), so the Enforce flip hides nothing from existing
-- members and no currently-permitted read is denied.
INSERT IGNORE INTO authz_organisation_role_assignments (user_id, role_key, organisation_id, created_at, updated_at)
SELECT u.id, 'compliance-reader', o.id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM users u
CROSS JOIN organisations o
WHERE u.global_role <> 'admin' AND u.enabled = 1 AND o.parent_id IS NULL;
