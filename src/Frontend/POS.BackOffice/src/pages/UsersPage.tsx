import { useEffect, useMemo, useState, type FormEvent } from "react";
import { ApiError, apiGet, apiPost } from "../api/client";
import { useOrganization } from "../api/organization";
import { useLanguage } from "../i18n/LanguageContext";

type ScopeType = "Company" | "Branch" | "Warehouse";

interface RoleAssignment {
  roleId: string;
  scopeType: ScopeType;
  scopeId: string;
}

interface UserSummary {
  id: string;
  email: string;
  displayName: string;
  status: "Active" | "Disabled";
  roleAssignments: RoleAssignment[];
}

interface InvitedUser {
  userId: string;
  email: string;
  temporaryPassword: string;
}

interface RoleSummary {
  id: string;
  name: string;
  description: string;
  isSystemRole: boolean;
  permissionCodes: string[];
}

interface PermissionSummary {
  code: string;
  module: string;
  description: string;
}

interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

const PAGE_SIZE = 20;

function statusBadgeClass(status: string): string {
  return status === "Active" ? "app-badge app-badge--positive" : "app-badge app-badge--negative";
}

function Pager({
  page,
  pageSize,
  totalCount,
  onChange,
}: {
  page: number;
  pageSize: number;
  totalCount: number;
  onChange: (page: number) => void;
}) {
  const { t } = useLanguage();
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  if (totalPages <= 1) return null;

  return (
    <div className="app-pager">
      <button
        type="button"
        className="app-button app-button--ghost"
        disabled={page <= 1}
        onClick={() => onChange(page - 1)}
      >
        {t("common.previous")}
      </button>
      <span className="app-pager__info">
        {t("common.pageOf", { page, totalPages: totalPages }) ||
          `${page} / ${totalPages}`}
      </span>
      <button
        type="button"
        className="app-button app-button--ghost"
        disabled={page >= totalPages}
        onClick={() => onChange(page + 1)}
      >
        {t("common.next")}
      </button>
    </div>
  );
}

export function UsersPage() {
  const { t } = useLanguage();
  const { companies } = useOrganization();

  const [users, setUsers] = useState<UserSummary[] | null>(null);
  const [userPage, setUserPage] = useState(1);
  const [userTotalCount, setUserTotalCount] = useState(0);

  const [roles, setRoles] = useState<RoleSummary[] | null>(null);
  const [rolePage, setRolePage] = useState(1);
  const [roleTotalCount, setRoleTotalCount] = useState(0);

  const [permissions, setPermissions] = useState<PermissionSummary[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // Invite form
  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteDisplayName, setInviteDisplayName] = useState("");
  const [inviteError, setInviteError] = useState<string | null>(null);
  const [isInviting, setIsInviting] = useState(false);
  const [lastInvite, setLastInvite] = useState<InvitedUser | null>(null);

  // Create-role form
  const [roleName, setRoleName] = useState("");
  const [roleDescription, setRoleDescription] = useState("");
  const [rolePermissionCodes, setRolePermissionCodes] = useState<Set<string>>(new Set());
  const [roleError, setRoleError] = useState<string | null>(null);
  const [isSavingRole, setIsSavingRole] = useState(false);

  // Assign-role form (one row of inputs, applied to whichever user is picked)
  const [assignUserId, setAssignUserId] = useState("");
  const [assignRoleId, setAssignRoleId] = useState("");
  const [assignScopeType, setAssignScopeType] = useState<ScopeType>("Branch");
  const [assignScopeId, setAssignScopeId] = useState("");
  const [assignError, setAssignError] = useState<string | null>(null);
  const [isAssigning, setIsAssigning] = useState(false);

  async function loadUsers(page: number) {
    try {
      const response = await apiGet<PagedResponse<UserSummary>>(
        `/api/v1/users?page=${page}&pageSize=${PAGE_SIZE}`,
      );
      setUsers(response.items);
      setUserTotalCount(response.totalCount);
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("users.loadUsersError"));
    }
  }

  async function loadRoles(page: number) {
    try {
      const response = await apiGet<PagedResponse<RoleSummary>>(
        `/api/v1/roles?page=${page}&pageSize=${PAGE_SIZE}`,
      );
      setRoles(response.items);
      setRoleTotalCount(response.totalCount);
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("users.loadRolesError"));
    }
  }

  async function loadPermissions() {
    try {
      setPermissions(await apiGet<PermissionSummary[]>("/api/v1/permissions"));
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("users.loadPermissionsError"));
    }
  }

  useEffect(() => {
    loadUsers(userPage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userPage]);

  useEffect(() => {
    loadRoles(rolePage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rolePage]);

  useEffect(() => {
    loadPermissions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Role names for the assignment badges need every role, not just the current
  // page — a role assigned to a user can easily live on a page the roles table
  // isn't currently showing. Resolved separately, once, rather than coupling the
  // badge labels to whichever page of GET /roles happens to be loaded.
  const [allRoles, setAllRoles] = useState<RoleSummary[] | null>(null);

  useEffect(() => {
    apiGet<PagedResponse<RoleSummary>>(`/api/v1/roles?page=1&pageSize=100`)
      .then((response) => setAllRoles(response.items))
      .catch(() => setAllRoles(null));
  }, []);

  const roleNameById = useMemo(
    () => new Map((allRoles ?? roles)?.map((r) => [r.id, r.name]) ?? []),
    [allRoles, roles],
  );

  const permissionsByModule = useMemo(() => {
    const groups = new Map<string, PermissionSummary[]>();
    for (const permission of permissions ?? []) {
      const group = groups.get(permission.module) ?? [];
      group.push(permission);
      groups.set(permission.module, group);
    }
    return groups;
  }, [permissions]);

  const scopeOptions = useMemo(() => {
    if (assignScopeType === "Company") return companies?.map((c) => ({ id: c.id, label: c.name })) ?? [];
    if (assignScopeType === "Branch")
      return companies?.flatMap((c) => c.branches.map((b) => ({ id: b.id, label: `${c.name} / ${b.name}` }))) ?? [];
    return (
      companies?.flatMap((c) =>
        c.branches.flatMap((b) => b.warehouses.map((w) => ({ id: w.id, label: `${c.name} / ${b.name} / ${w.name}` }))),
      ) ?? []
    );
  }, [companies, assignScopeType]);

  async function handleInvite(event: FormEvent) {
    event.preventDefault();
    setInviteError(null);
    setIsInviting(true);

    try {
      const invited = await apiPost<InvitedUser>("/api/v1/users", {
        email: inviteEmail,
        displayName: inviteDisplayName,
      });

      setLastInvite(invited);
      setInviteEmail("");
      setInviteDisplayName("");
      await loadUsers(userPage);
    } catch (err) {
      setInviteError(err instanceof ApiError ? err.message : t("users.inviteError"));
    } finally {
      setIsInviting(false);
    }
  }

  function togglePermission(code: string) {
    setRolePermissionCodes((current) => {
      const next = new Set(current);
      if (next.has(code)) next.delete(code);
      else next.add(code);
      return next;
    });
  }

  async function handleCreateRole(event: FormEvent) {
    event.preventDefault();
    setRoleError(null);
    setIsSavingRole(true);

    try {
      await apiPost("/api/v1/roles", {
        name: roleName,
        description: roleDescription,
        permissionCodes: [...rolePermissionCodes],
      });

      setRoleName("");
      setRoleDescription("");
      setRolePermissionCodes(new Set());
      await loadRoles(rolePage);
    } catch (err) {
      setRoleError(err instanceof ApiError ? err.message : t("users.createError"));
    } finally {
      setIsSavingRole(false);
    }
  }

  async function handleAssign(event: FormEvent) {
    event.preventDefault();
    setAssignError(null);
    setIsAssigning(true);

    try {
      await apiPost(`/api/v1/users/${assignUserId}/roles`, {
        roleId: assignRoleId,
        scopeType: assignScopeType,
        scopeId: assignScopeId,
      });

      setAssignRoleId("");
      setAssignScopeId("");
      await loadUsers(userPage);
    } catch (err) {
      setAssignError(err instanceof ApiError ? err.message : t("users.assignError"));
    } finally {
      setIsAssigning(false);
    }
  }

  async function handleRevoke(userId: string, assignment: RoleAssignment) {
    setActionError(null);
    try {
      await apiPost(`/api/v1/users/${userId}/roles/revoke`, {
        roleId: assignment.roleId,
        scopeType: assignment.scopeType,
        scopeId: assignment.scopeId,
      });
      await loadUsers(userPage);
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("users.revokeError"));
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("users.title")}</h1>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("users.inviteUser")}</h2>
        {inviteError && <div className="app-error-banner">{inviteError}</div>}
        {lastInvite && (
          <div className="app-error-banner" style={{ background: "var(--app-positive-bg, #e6f4ea)" }}>
            <strong>{lastInvite.email}</strong> {t("users.wasInvited")} <code>{lastInvite.temporaryPassword}</code>{" "}
            <button type="button" className="app-button app-button--ghost" onClick={() => setLastInvite(null)}>
              {t("common.dismiss")}
            </button>
          </div>
        )}
        <form onSubmit={handleInvite}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="invite-email">{t("common.email")}</label>
              <input
                id="invite-email"
                type="email"
                value={inviteEmail}
                onChange={(e) => setInviteEmail(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="invite-display-name">{t("users.displayName")}</label>
              <input
                id="invite-display-name"
                value={inviteDisplayName}
                onChange={(e) => setInviteDisplayName(e.target.value)}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isInviting}>
            {isInviting ? t("users.inviting") : t("users.inviteUser")}
          </button>
        </form>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("users.assignRole")}</h2>
        {assignError && <div className="app-error-banner">{assignError}</div>}
        <form onSubmit={handleAssign}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="assign-user">{t("users.user")}</label>
              <select id="assign-user" value={assignUserId} onChange={(e) => setAssignUserId(e.target.value)} required>
                <option value="" disabled>
                  {t("users.selectUser")}
                </option>
                {users?.map((user) => (
                  <option key={user.id} value={user.id}>
                    {user.displayName} ({user.email})
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="assign-role">{t("users.role")}</label>
              <select id="assign-role" value={assignRoleId} onChange={(e) => setAssignRoleId(e.target.value)} required>
                <option value="" disabled>
                  {t("users.selectRole")}
                </option>
                {(allRoles ?? roles)?.map((role) => (
                  <option key={role.id} value={role.id}>
                    {role.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="assign-scope-type">{t("users.scope")}</label>
              <select
                id="assign-scope-type"
                value={assignScopeType}
                onChange={(e) => {
                  setAssignScopeType(e.target.value as ScopeType);
                  setAssignScopeId("");
                }}
              >
                <option value="Company">{t("users.scopeCompany")}</option>
                <option value="Branch">{t("users.scopeBranch")}</option>
                <option value="Warehouse">{t("users.scopeWarehouse")}</option>
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="assign-scope-id">{t("users.at")}</label>
              <select
                id="assign-scope-id"
                value={assignScopeId}
                onChange={(e) => setAssignScopeId(e.target.value)}
                required
              >
                <option value="" disabled>
                  {t("common.selectEllipsis")}
                </option>
                {scopeOptions.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isAssigning}>
            {isAssigning ? t("users.assigning") : t("users.assignRole")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {listError && <div className="app-error-banner">{listError}</div>}
        {actionError && <div className="app-error-banner">{actionError}</div>}

        {users !== null && users.length === 0 && <div className="app-empty-state">{t("users.empty")}</div>}

        {users !== null && users.length > 0 && (
          <>
            <table className="app-table">
              <thead>
                <tr>
                  <th>{t("users.colEmail")}</th>
                  <th>{t("users.colDisplayName")}</th>
                  <th>{t("users.colStatus")}</th>
                  <th>{t("users.colRoles")}</th>
                </tr>
              </thead>
              <tbody>
                {users.map((user) => (
                  <tr key={user.id}>
                    <td>{user.email}</td>
                    <td>{user.displayName}</td>
                    <td>
                      <span className={statusBadgeClass(user.status)}>
                        {user.status === "Active" ? t("users.statusActive") : t("users.statusDisabled")}
                      </span>
                    </td>
                    <td style={{ display: "flex", flexWrap: "wrap", gap: "0.4rem" }}>
                      {user.roleAssignments.length === 0 && <span>—</span>}
                      {user.roleAssignments.map((assignment, index) => (
                        <span key={index} className="app-badge">
                          {roleNameById.get(assignment.roleId) ?? assignment.roleId} @ {assignment.scopeType}
                          <button
                            type="button"
                            className="app-button app-button--ghost"
                            style={{ marginLeft: "0.4rem", padding: "0 0.4rem" }}
                            onClick={() => handleRevoke(user.id, assignment)}
                          >
                            {t("users.revoke")}
                          </button>
                        </span>
                      ))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={userPage} pageSize={PAGE_SIZE} totalCount={userTotalCount} onChange={setUserPage} />
          </>
        )}
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("users.createRole")}</h2>
        {roleError && <div className="app-error-banner">{roleError}</div>}
        <form onSubmit={handleCreateRole}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="role-name">{t("users.roleName")}</label>
              <input id="role-name" value={roleName} onChange={(e) => setRoleName(e.target.value)} required />
            </div>
            <div className="app-form-field">
              <label htmlFor="role-description">{t("users.roleDescription")}</label>
              <input
                id="role-description"
                value={roleDescription}
                onChange={(e) => setRoleDescription(e.target.value)}
                required
              />
            </div>
          </div>
          <div className="app-form-field">
            <label>{t("users.permissions")}</label>
            {[...permissionsByModule.entries()].map(([module, items]) => (
              <fieldset key={module} style={{ marginBottom: "0.75rem" }}>
                <legend>{module}</legend>
                {items.map((permission) => (
                  <label key={permission.code} style={{ display: "block" }}>
                    <input
                      type="checkbox"
                      checked={rolePermissionCodes.has(permission.code)}
                      onChange={() => togglePermission(permission.code)}
                    />{" "}
                    {permission.code} — {permission.description}
                  </label>
                ))}
              </fieldset>
            ))}
          </div>
          <button type="submit" className="app-button" disabled={isSavingRole}>
            {isSavingRole ? t("products.creating") : t("users.createRole")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {roles !== null && roles.length === 0 && <div className="app-empty-state">{t("users.rolesEmpty")}</div>}

        {roles !== null && roles.length > 0 && (
          <>
            <table className="app-table">
              <thead>
                <tr>
                  <th>{t("common.name")}</th>
                  <th>{t("common.description")}</th>
                  <th>{t("users.colSystemRole")}</th>
                  <th>{t("users.colPermissions")}</th>
                </tr>
              </thead>
              <tbody>
                {roles.map((role) => (
                  <tr key={role.id}>
                    <td>{role.name}</td>
                    <td>{role.description}</td>
                    <td>{role.isSystemRole ? t("common.yes") : t("common.no")}</td>
                    <td>{role.permissionCodes.length}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={rolePage} pageSize={PAGE_SIZE} totalCount={roleTotalCount} onChange={setRolePage} />
          </>
        )}
      </div>
    </div>
  );
}
