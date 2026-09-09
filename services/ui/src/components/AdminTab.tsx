import React, { FormEvent, useEffect, useMemo, useState } from "react";
import {
  ActionBar,
  Badge,
  Box,
  Button,
  Checkbox,
  Dialog,
  Flex,
  HStack,
  IconButton,
  Input,
  Menu,
  NativeSelect,
  Portal,
  Spinner,
  Table,
  Text,
  VStack,
} from "@chakra-ui/react";
import { KeyRound, MoreVertical, Trash2, Shield } from "lucide-react";
import { JOB_ROLES, User, createUser, deleteUser, listUsers, patchUser, setUserPassword } from "../lib/auth";
import { useAuth } from "../hooks/useAuth";
import { EvalDiagnostics } from "./EvalDiagnostics";
import { AiGuardDiagnostics } from "./AiGuardDiagnostics";
import { PromptDiagnostics } from "./PromptDiagnostics";

// ── Checkbox helper ───────────────────────────────────────────────────────────

interface NativeCheckboxProps {
  id: string;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

function NativeCheckbox({ id, label, checked, onChange, disabled }: NativeCheckboxProps) {
  return (
    <Box as="label" display="flex" alignItems="center" gap={2} cursor={disabled ? "not-allowed" : "pointer"}>
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        style={{ width: 14, height: 14, cursor: disabled ? "not-allowed" : "pointer" }}
      />
      <Text fontSize="sm" color="gray.700" userSelect="none">{label}</Text>
    </Box>
  );
}

// ── Pending action type ───────────────────────────────────────────────────────
// `users` is always an array — a single-row action just passes a one-element
// array — so the confirmation dialog and handleConfirm have one code path
// for both single and bulk (Action Bar) actions instead of two.

type PendingAction =
  | { kind: "grant-admin"; users: User[] }
  | { kind: "revoke-admin"; users: User[] }
  | { kind: "delete"; users: User[] }
  | null;

// ── Main component ────────────────────────────────────────────────────────────

export function AdminTab() {
  const { user: currentUser } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [loadingUsers, setLoadingUsers] = useState(true);
  const [listError, setListError] = useState<string | null>(null);
  const [patchingUserId, setPatchingUserId] = useState<string | null>(null);

  // Bulk selection — the current admin's own row is never selectable (every
  // bulk action below is either destructive or an admin-role change, both
  // already blocked for self at the single-row level).
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkJobRole, setBulkJobRole] = useState("");
  const [bulkBusy, setBulkBusy] = useState(false);

  // Add user form
  const [newEmail, setNewEmail] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [newIsAdmin, setNewIsAdmin] = useState(false);
  const [newIsService, setNewIsService] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  // Passwords live only in this dialog's component state and are cleared as
  // soon as the dialog closes. They are never copied into the user list,
  // browser storage, logs, RUM attributes, or API responses.
  const [passwordUser, setPasswordUser] = useState<User | null>(null);
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [passwordSuccess, setPasswordSuccess] = useState<string | null>(null);
  const [savingPassword, setSavingPassword] = useState(false);

  // Confirmation dialog
  const [pendingAction, setPendingAction] = useState<PendingAction>(null);
  const [confirming, setConfirming] = useState(false);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  async function loadUsers() {
    setLoadingUsers(true);
    setListError(null);
    try {
      setUsers(await listUsers());
    } catch (err) {
      setListError(err instanceof Error ? err.message : "Failed to load users");
    } finally {
      setLoadingUsers(false);
    }
  }

  useEffect(() => { loadUsers(); }, []);

  // ── Bulk selection ────────────────────────────────────────────────────────

  const selectableUsers = useMemo(() => users.filter((u) => u.id !== currentUser?.id), [users, currentUser]);
  const selectedUsers = useMemo(() => users.filter((u) => selectedIds.has(u.id)), [users, selectedIds]);
  const allSelectableSelected = selectableUsers.length > 0 && selectableUsers.every((u) => selectedIds.has(u.id));
  const someSelected = selectedIds.size > 0;

  function toggleSelected(id: string, checked: boolean) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (checked) next.add(id); else next.delete(id);
      return next;
    });
  }

  function toggleSelectAll(checked: boolean) {
    setSelectedIds(checked ? new Set(selectableUsers.map((u) => u.id)) : new Set());
  }

  function clearSelection() {
    setSelectedIds(new Set());
    setBulkJobRole("");
  }

  async function handleBulkJobRole(jobRole: string) {
    setBulkJobRole(jobRole);
    if (!jobRole || selectedUsers.length === 0) return;
    setBulkBusy(true);
    try {
      const updated = await Promise.all(selectedUsers.map((u) => patchUser(u.id, { job_role: jobRole })));
      setUsers((prev) => prev.map((u) => updated.find((x) => x.id === u.id) ?? u));
    } catch (err) {
      setListError(err instanceof Error ? err.message : "Failed to update job role for selected users");
    } finally {
      setBulkBusy(false);
      setBulkJobRole("");
    }
  }

  // ── Add user ──────────────────────────────────────────────────────────────

  async function handleAddUser(e: FormEvent) {
    e.preventDefault();
    setAddError(null);
    setAdding(true);
    try {
      const created = await createUser({
        email: newEmail,
        password: newPassword,
        is_admin: newIsAdmin,
        is_service_account: newIsService,
      });
      setUsers((prev) => [...prev, created]);
      setNewEmail("");
      setNewPassword("");
      setNewIsAdmin(false);
      setNewIsService(false);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Failed to create user");
    } finally {
      setAdding(false);
    }
  }

  // ── Set password ─────────────────────────────────────────────────────────

  function openPasswordDialog(user: User) {
    setPassword("");
    setConfirmPassword("");
    setPasswordError(null);
    setPasswordSuccess(null);
    setPasswordUser(user);
  }

  function closePasswordDialog() {
    if (savingPassword) return;
    setPasswordUser(null);
    setPassword("");
    setConfirmPassword("");
    setPasswordError(null);
  }

  async function handleSetPassword(e: FormEvent) {
    e.preventDefault();
    if (!passwordUser) return;
    if (password.length < 8) {
      setPasswordError("Password must be at least 8 characters.");
      return;
    }
    if (password !== confirmPassword) {
      setPasswordError("Passwords do not match.");
      return;
    }

    setSavingPassword(true);
    setPasswordError(null);
    try {
      await setUserPassword(passwordUser.id, password);
      setPasswordSuccess("Password updated successfully.");
      setPasswordUser(null);
      setPassword("");
      setConfirmPassword("");
    } catch (err) {
      setPasswordError(err instanceof Error ? err.message : "Failed to set password");
    } finally {
      setSavingPassword(false);
    }
  }

  // ── Confirmation dialog ───────────────────────────────────────────────────

  function openConfirm(action: PendingAction) {
    setConfirmError(null);
    setPendingAction(action);
  }

  function closeConfirm() {
    if (confirming) return;
    setPendingAction(null);
    setConfirmError(null);
  }

  async function handleConfirm() {
    if (!pendingAction) return;
    const ids = pendingAction.users.map((u) => u.id);
    setConfirming(true);
    setConfirmError(null);
    try {
      if (pendingAction.kind === "delete") {
        await Promise.all(ids.map((id) => deleteUser(id)));
        setUsers((prev) => prev.filter((u) => !ids.includes(u.id)));
      } else {
        const isAdmin = pendingAction.kind === "grant-admin";
        const updated = await Promise.all(ids.map((id) => patchUser(id, { is_admin: isAdmin })));
        setUsers((prev) => prev.map((u) => updated.find((x) => x.id === u.id) ?? u));
      }
      setPendingAction(null);
      clearSelection();
    } catch (err) {
      setConfirmError(err instanceof Error ? err.message : "Action failed");
    } finally {
      setConfirming(false);
    }
  }

  // ── Dialog copy ───────────────────────────────────────────────────────────

  function dialogSubject(): string {
    if (!pendingAction) return "";
    if (pendingAction.users.length === 1) return pendingAction.users[0].email;
    return `${pendingAction.users.length} users`;
  }

  function dialogTitle(): string {
    if (!pendingAction) return "";
    const subject = dialogSubject();
    if (pendingAction.kind === "grant-admin") return `Grant admin to ${subject}?`;
    if (pendingAction.kind === "revoke-admin") return `Remove admin from ${subject}?`;
    return `Delete ${subject}?`;
  }

  function dialogBody(): string {
    if (!pendingAction) return "";
    const subject = dialogSubject();
    const plural = pendingAction.users.length > 1;
    if (pendingAction.kind === "grant-admin")
      return `${subject} will be able to manage all users and access all admin features.`;
    if (pendingAction.kind === "revoke-admin")
      return `${subject} will lose admin access and be downgraded to standard user${plural ? "s" : ""}.`;
    return `The account${plural ? "s" : ""} for ${subject} will be permanently deleted. This cannot be undone.`;
  }

  function dialogConfirmLabel(): string {
    if (!pendingAction) return "Confirm";
    if (pendingAction.kind === "grant-admin") return "Grant admin";
    if (pendingAction.kind === "revoke-admin") return "Remove admin";
    return "Delete user";
  }

  function dialogConfirmColor(): string {
    if (pendingAction?.kind === "grant-admin") return "blue";
    return "red";
  }

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <Box flex={1} overflowY="auto" px={6} py={6}>
      <VStack gap={6} align="stretch" maxW="4xl" mx="auto">

        {/* Header */}
        <Box>
          <Text fontSize="lg" fontWeight="semibold" color="gray.800">User Management</Text>
          <Text fontSize="sm" color="gray.400" mt={0.5}>Manage user accounts and permissions</Text>
        </Box>

        {/* Add user form */}
        <Box bg="white" borderWidth="1px" borderColor="gray.200" borderRadius="xl" p={5} boxShadow="xs">
          <Text fontSize="sm" fontWeight="semibold" color="gray.700" mb={4}>Add user</Text>
          <form onSubmit={handleAddUser}>
            <VStack gap={3} align="stretch">
              <HStack gap={3} align="flex-end">
                <Box flex={1}>
                  <Text fontSize="xs" fontWeight="medium" color="gray.600" mb={1}>Email</Text>
                  <Input
                    type="email"
                    value={newEmail}
                    onChange={(e) => setNewEmail(e.target.value)}
                    placeholder={newIsService ? "service@example.com" : "user@datadoghq.com"}
                    required
                    disabled={adding}
                    fontSize="sm"
                    borderRadius="lg"
                    borderColor="gray.300"
                    _focus={{ borderColor: "blue.500", boxShadow: "0 0 0 1px var(--chakra-colors-blue-500)" }}
                  />
                </Box>
                <Box flex={1}>
                  <Text fontSize="xs" fontWeight="medium" color="gray.600" mb={1}>Password</Text>
                  <Input
                    type="password"
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                    placeholder="••••••••"
                    minLength={8}
                    required
                    disabled={adding}
                    fontSize="sm"
                    borderRadius="lg"
                    borderColor="gray.300"
                    _focus={{ borderColor: "blue.500", boxShadow: "0 0 0 1px var(--chakra-colors-blue-500)" }}
                  />
                </Box>
              </HStack>
              <HStack gap={6}>
                <NativeCheckbox id="new-is-admin" label="Admin" checked={newIsAdmin}
                  onChange={setNewIsAdmin} disabled={adding} />
                <NativeCheckbox id="new-is-service" label="Service account" checked={newIsService}
                  onChange={setNewIsService} disabled={adding} />
                <Text fontSize="xs" color="gray.400">
                  {newIsService ? "Service accounts may use any email domain." : "Regular accounts require @datadoghq.com."}
                </Text>
              </HStack>
              {addError && <Text fontSize="sm" color="red.500">{addError}</Text>}
              <Box>
                <Button type="submit" colorPalette="blue" size="sm" borderRadius="lg" disabled={adding}>
                  {adding ? <Spinner size="xs" /> : "Add user"}
                </Button>
              </Box>
            </VStack>
          </form>
        </Box>

        {passwordSuccess && (
          <Box role="status" bg="green.50" borderWidth="1px" borderColor="green.200" borderRadius="lg" px={4} py={3}>
            <Text fontSize="sm" color="green.700">{passwordSuccess}</Text>
          </Box>
        )}

        {/* Users table */}
        <Box bg="white" borderWidth="1px" borderColor="gray.200" borderRadius="xl" boxShadow="xs" overflowX="auto">
          {loadingUsers ? (
            <Flex justify="center" align="center" py={10}><Spinner size="sm" color="blue.500" /></Flex>
          ) : listError ? (
            <Flex justify="center" align="center" py={10}>
              <Text fontSize="sm" color="red.500">{listError}</Text>
            </Flex>
          ) : (
            <Table.Root size="sm" minW="820px">
              <Table.Header>
                <Table.Row bg="gray.50">
                  <Table.ColumnHeader py={3} px={4} w="10">
                    <Checkbox.Root
                      size="sm"
                      checked={allSelectableSelected ? true : someSelected ? "indeterminate" : false}
                      disabled={selectableUsers.length === 0}
                      onCheckedChange={(e) => toggleSelectAll(!!e.checked)}
                      aria-label="Select all users"
                    >
                      <Checkbox.HiddenInput />
                      <Checkbox.Control />
                    </Checkbox.Root>
                  </Table.ColumnHeader>
                  {["Email", "Roles", "Job Role", "Created", "Actions"].map((h) => (
                    <Table.ColumnHeader key={h} fontSize="xs" fontWeight="semibold" color="gray.500"
                      textTransform="uppercase" letterSpacing="wider" py={3} px={4}>
                      {h}
                    </Table.ColumnHeader>
                  ))}
                </Table.Row>
              </Table.Header>
              <Table.Body>
                {users.length === 0 ? (
                  <Table.Row>
                    <Table.Cell colSpan={6} textAlign="center" py={8} color="gray.400" fontSize="sm">
                      No users found
                    </Table.Cell>
                  </Table.Row>
                ) : (
                  users.map((u) => {
                    const isSelf = u.id === currentUser?.id;
                    return (
                      <Table.Row key={u.id} _hover={{ bg: "gray.50" }}>

                        {/* Select — the current admin's own row is never
                            selectable, matching the per-row restrictions
                            already in place for admin/delete actions. */}
                        <Table.Cell px={4} py={3}>
                          <Checkbox.Root
                            size="sm"
                            checked={selectedIds.has(u.id)}
                            disabled={isSelf}
                            onCheckedChange={(e) => toggleSelected(u.id, !!e.checked)}
                            aria-label={`Select ${u.email}`}
                          >
                            <Checkbox.HiddenInput />
                            <Checkbox.Control />
                          </Checkbox.Root>
                        </Table.Cell>

                        {/* Email */}
                        <Table.Cell px={4} py={3}>
                          <HStack gap={2}>
                            <Text fontSize="sm" color="gray.800">{u.email}</Text>
                            {isSelf && (
                              <Badge colorPalette="blue" variant="subtle" fontSize="2xs" borderRadius="full" px={1.5}>
                                you
                              </Badge>
                            )}
                          </HStack>
                        </Table.Cell>

                        {/* Roles */}
                        <Table.Cell px={4} py={3}>
                          <HStack gap={1.5}>
                            {u.is_admin && (
                              <Badge colorPalette="purple" variant="subtle" fontSize="xs" borderRadius="full" px={2}>
                                Admin
                              </Badge>
                            )}
                            {u.is_service_account && (
                              <Badge colorPalette="orange" variant="subtle" fontSize="xs" borderRadius="full" px={2}>
                                Service
                              </Badge>
                            )}
                            {!u.is_admin && !u.is_service_account && (
                              <Text fontSize="xs" color="gray.400">User</Text>
                            )}
                          </HStack>
                        </Table.Cell>

                        {/* Job role — demo OpenFeature targeting attribute, embedded in
                            the JWT at next login. Editable inline, including for the
                            current admin's own account (unlike the admin/service-account
                            toggles below, this is a benign demo attribute). */}
                        <Table.Cell px={4} py={3}>
                          <NativeSelect.Root size="xs" disabled={patchingUserId === u.id}>
                            <NativeSelect.Field
                              aria-label={`Job role for ${u.email}`}
                              value={u.job_role ?? ""}
                              onChange={async (e) => {
                                const jobRole = e.target.value;
                                setPatchingUserId(u.id);
                                try {
                                  const updated = await patchUser(u.id, { job_role: jobRole });
                                  setUsers((prev) => prev.map((x) => (x.id === updated.id ? updated : x)));
                                } catch (err) {
                                  setListError(err instanceof Error ? err.message : "Failed to update job role");
                                } finally {
                                  setPatchingUserId(null);
                                }
                              }}
                              fontFamily="mono"
                              fontSize="xs"
                            >
                              {JOB_ROLES.map((role) => (
                                <option key={role} value={role}>{role}</option>
                              ))}
                            </NativeSelect.Field>
                            <NativeSelect.Indicator />
                          </NativeSelect.Root>
                        </Table.Cell>

                        {/* Created */}
                        <Table.Cell px={4} py={3}>
                          <Text fontSize="xs" color="gray.400">
                            {new Date(u.created_at).toLocaleDateString(undefined, {
                              year: "numeric", month: "short", day: "numeric",
                            })}
                          </Text>
                        </Table.Cell>

                        {/* Actions — a single grouped menu instead of three
                            separate row buttons. */}
                        <Table.Cell px={4} py={3}>
                          <Menu.Root>
                            <Menu.Trigger asChild>
                              <IconButton
                                size="xs"
                                variant="ghost"
                                colorPalette="gray"
                                borderRadius="md"
                                aria-label={`Actions for ${u.email}`}
                              >
                                <MoreVertical size={14} />
                              </IconButton>
                            </Menu.Trigger>
                            <Portal>
                              <Menu.Positioner>
                                <Menu.Content minW="10rem">
                                  <Menu.Item
                                    value="password"
                                    onSelect={() => openPasswordDialog(u)}
                                  >
                                    <KeyRound size={13} />
                                    Set password
                                  </Menu.Item>
                                  <Menu.Item
                                    value="admin-toggle"
                                    disabled={isSelf}
                                    onSelect={() =>
                                      openConfirm(
                                        u.is_admin
                                          ? { kind: "revoke-admin", users: [u] }
                                          : { kind: "grant-admin", users: [u] }
                                      )
                                    }
                                  >
                                    <Shield size={13} />
                                    {u.is_admin ? "Revoke admin" : "Grant admin"}
                                  </Menu.Item>
                                  <Menu.Separator />
                                  <Menu.Item
                                    value="delete"
                                    disabled={isSelf}
                                    color="fg.error"
                                    _hover={{ bg: "bg.error", color: "fg.error" }}
                                    onSelect={() => openConfirm({ kind: "delete", users: [u] })}
                                  >
                                    <Trash2 size={13} />
                                    Delete user
                                  </Menu.Item>
                                </Menu.Content>
                              </Menu.Positioner>
                            </Portal>
                          </Menu.Root>
                        </Table.Cell>

                      </Table.Row>
                    );
                  })
                )}
              </Table.Body>
            </Table.Root>
          )}
        </Box>

        <Text fontSize="xs" color="gray.400" textAlign="center">
          Administrators can set any account password, including their own. Your own role and account cannot be removed.
        </Text>

        {/* ── Eval pipeline diagnostics (read-only) ──────────────────────
            Surfaces the same /eval/status snapshot that ops uses from the
            CLI. Read-only by design: env-driven config (EVAL_SAMPLE_RATE,
            DD_API_KEY) means runtime mutation would diverge from pod
            restart-time truth. See docs/llm-observability-dotnet.md. */}
        <Box>
          <Text fontSize="sm" fontWeight="semibold" color="gray.700" mb={1}>
            Eval pipeline (read-only)
          </Text>
          <Text fontSize="xs" color="gray.500" mb={3}>
            Snapshot of the .NET agent's external-evaluations pipeline. To change sample rate or judge model, update env vars and restart the agent-api-dotnet pod.
          </Text>
          <EvalDiagnostics />
        </Box>

        {/* ── AI Guard diagnostics (read-only) ────────────────────────────
            AI Guard's HTTP API path (the only option for the .NET/MAF
            backend) sends no traces to Datadog, so this in-app panel is
            the only visibility into whether it's running and what it's
            deciding. See docs/llm-engineering/ai-guard.mdx. */}
        <Box>
          <Text fontSize="sm" fontWeight="semibold" color="gray.700" mb={1}>
            AI Guard (read-only)
          </Text>
          <Text fontSize="xs" color="gray.500" mb={3}>
            Snapshot of the .NET agent's AI Guard pre-flight checks. Native Datadog trace visibility isn't available over the HTTP API — this panel plus the manual ai_guard.* APM span tags are the substitute.
          </Text>
          <AiGuardDiagnostics />
        </Box>

        {/* ── Prompt versions (read-only) ──────────────────────────────────
            Shows each subagent's currently active prompt version across both
            backends — Python's router+specialists (tool-partitioned, one
            prompt_id each) and .NET's single merged agent. A version can be
            pinned per environment via a prompt-version.<prompt_id> Feature
            Flag; "flag-pinned" in the Source column reflects that override.
            See docs/llm-engineering/monitoring/prompt-targeting.mdx. */}
        <Box>
          <Text fontSize="sm" fontWeight="semibold" color="gray.700" mb={1}>
            Prompt versions (read-only)
          </Text>
          <Text fontSize="xs" color="gray.500" mb={3}>
            Active prompt version per subagent, across both backends. Deploy a new version by pointing a prompt-version.&lt;prompt_id&gt; Feature Flag at it in Datadog — no redeploy needed.
          </Text>
          <PromptDiagnostics />
        </Box>
      </VStack>

      {/* Password dialog stays separate from role/delete confirmation so a
          secret value can never enter the generic pending-action model. */}
      <Dialog.Root open={passwordUser !== null} onOpenChange={(e) => !e.open && closePasswordDialog()}>
        <Dialog.Backdrop />
        <Dialog.Positioner>
          <Dialog.Content borderRadius="xl" maxW="sm" mx={4}>
            <form onSubmit={handleSetPassword}>
              <Dialog.Header px={6} pt={6} pb={2}>
                <Dialog.Title fontSize="md" fontWeight="semibold" color="gray.800">
                  Set password
                </Dialog.Title>
              </Dialog.Header>

              <Dialog.Body px={6} py={3}>
                <VStack gap={4} align="stretch">
                  <Text fontSize="sm" color="gray.600">
                    Set a new password for {passwordUser?.email}. Existing signed-in sessions remain active.
                  </Text>
                  <Box>
                    <label htmlFor="admin-new-password" style={{ display: "block", marginBottom: 4, fontSize: "0.75rem", fontWeight: 500, color: "var(--chakra-colors-gray-600)" }}>
                      New password
                    </label>
                    <Input
                      id="admin-new-password"
                      type="password"
                      autoComplete="new-password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      minLength={8}
                      autoFocus
                      required
                      disabled={savingPassword}
                      borderRadius="lg"
                    />
                  </Box>
                  <Box>
                    <label htmlFor="admin-confirm-password" style={{ display: "block", marginBottom: 4, fontSize: "0.75rem", fontWeight: 500, color: "var(--chakra-colors-gray-600)" }}>
                      Confirm password
                    </label>
                    <Input
                      id="admin-confirm-password"
                      type="password"
                      autoComplete="new-password"
                      value={confirmPassword}
                      onChange={(e) => setConfirmPassword(e.target.value)}
                      minLength={8}
                      required
                      disabled={savingPassword}
                      borderRadius="lg"
                    />
                  </Box>
                  <Text fontSize="xs" color="gray.500">Use at least 8 characters. Any outstanding email reset link will be invalidated.</Text>
                  {passwordError && <Text role="alert" fontSize="sm" color="red.500">{passwordError}</Text>}
                </VStack>
              </Dialog.Body>

              <Dialog.Footer px={6} pb={6} pt={4} gap={3}>
                <Button type="button" size="sm" variant="ghost" colorPalette="gray" borderRadius="lg" disabled={savingPassword} onClick={closePasswordDialog}>
                  Cancel
                </Button>
                <Button type="submit" size="sm" colorPalette="blue" borderRadius="lg" disabled={savingPassword}>
                  {savingPassword ? <Spinner size="xs" /> : "Set password"}
                </Button>
              </Dialog.Footer>
            </form>
          </Dialog.Content>
        </Dialog.Positioner>
      </Dialog.Root>

      {/* Confirmation dialog */}
      <Dialog.Root open={pendingAction !== null} onOpenChange={(e) => !e.open && closeConfirm()}>
        <Dialog.Backdrop />
        <Dialog.Positioner>
          <Dialog.Content borderRadius="xl" maxW="sm" mx={4}>
            <Dialog.Header px={6} pt={6} pb={2}>
              <Dialog.Title fontSize="md" fontWeight="semibold" color="gray.800">
                {dialogTitle()}
              </Dialog.Title>
            </Dialog.Header>

            <Dialog.Body px={6} py={3}>
              <Text fontSize="sm" color="gray.600">{dialogBody()}</Text>
              {confirmError && (
                <Text fontSize="sm" color="red.500" mt={3}>{confirmError}</Text>
              )}
            </Dialog.Body>

            <Dialog.Footer px={6} pb={6} pt={4} gap={3}>
              <Button
                size="sm"
                variant="ghost"
                colorPalette="gray"
                borderRadius="lg"
                disabled={confirming}
                onClick={closeConfirm}
              >
                Cancel
              </Button>
              <Button
                size="sm"
                colorPalette={dialogConfirmColor()}
                borderRadius="lg"
                disabled={confirming}
                onClick={handleConfirm}
              >
                {confirming ? <Spinner size="xs" /> : dialogConfirmLabel()}
              </Button>
            </Dialog.Footer>
          </Dialog.Content>
        </Dialog.Positioner>
      </Dialog.Root>

      {/* Bulk actions — appears once at least one (non-self) row is
          selected. Job role is applied immediately (a benign demo
          attribute, same reasoning as the inline per-row select above);
          admin/delete route through the same confirmation dialog as their
          single-row equivalents. */}
      <ActionBar.Root open={someSelected} onOpenChange={(e) => !e.open && clearSelection()}>
        <Portal>
          <ActionBar.Positioner>
            <ActionBar.Content>
              <ActionBar.SelectionTrigger>
                {selectedIds.size} selected
              </ActionBar.SelectionTrigger>
              <ActionBar.Separator />

              <NativeSelect.Root size="xs" disabled={bulkBusy} minW="9rem">
                <NativeSelect.Field
                  aria-label="Set job role for selected users"
                  value={bulkJobRole}
                  onChange={(e) => handleBulkJobRole(e.target.value)}
                  fontSize="xs"
                >
                  <option value="" disabled>Set job role…</option>
                  {JOB_ROLES.map((role) => (
                    <option key={role} value={role}>{role}</option>
                  ))}
                </NativeSelect.Field>
                <NativeSelect.Indicator />
              </NativeSelect.Root>

              <Button
                size="sm"
                variant="outline"
                colorPalette="purple"
                disabled={bulkBusy}
                onClick={() => openConfirm({ kind: "grant-admin", users: selectedUsers })}
              >
                <Shield size={13} />
                Grant admin
              </Button>
              <Button
                size="sm"
                variant="outline"
                colorPalette="orange"
                disabled={bulkBusy}
                onClick={() => openConfirm({ kind: "revoke-admin", users: selectedUsers })}
              >
                <Shield size={13} />
                Revoke admin
              </Button>
              <Button
                size="sm"
                variant="surface"
                colorPalette="red"
                disabled={bulkBusy}
                onClick={() => openConfirm({ kind: "delete", users: selectedUsers })}
              >
                <Trash2 size={13} />
                Delete
              </Button>

              <ActionBar.CloseTrigger asChild>
                <IconButton size="sm" variant="ghost" aria-label="Clear selection" onClick={clearSelection}>
                  ×
                </IconButton>
              </ActionBar.CloseTrigger>
            </ActionBar.Content>
          </ActionBar.Positioner>
        </Portal>
      </ActionBar.Root>
    </Box>
  );
}
