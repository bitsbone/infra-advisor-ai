import { useEffect, useState } from "react";
import { Badge, Box, Flex, HStack, Spinner, Table, Text, VStack } from "@chakra-ui/react";
import { Layers } from "lucide-react";
import { getApiBase } from "../lib/api";
import { getToken } from "../lib/auth";

// ── Types matching GET /admin/prompts/status (agent-api) and
//    GET /prompts/status (agent-api-dotnet) ─────────────────────────────────

interface PromptRow {
  prompt_id: string;
  backend: "python" | "dotnet";
  version: string | number | null;
  source: string;
  flag_value: number;
}

// Polling cadence matches EvalDiagnostics — cheap endpoints, admins are
// expected to glance rather than stare.
const POLL_INTERVAL_MS = 10_000;

async function fetchStatus(backend: "python" | "dotnet"): Promise<PromptRow[]> {
  const url =
    backend === "python"
      ? `${getApiBase("python")}/admin/prompts/status`
      : `${getApiBase("dotnet")}/prompts/status`;
  // agent-api's /admin/prompts/status requires Authorization: Bearer <jwt> —
  // this app authenticates via a token header, not cookies, so
  // credentials: "include" alone (copied from EvalDiagnostics, whose .NET
  // endpoint has no auth requirement) always 401'd here silently.
  const token = getToken();
  const resp = await fetch(url, {
    credentials: "include",
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  });
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  return resp.json();
}

export function PromptDiagnostics() {
  const [rows, setRows] = useState<PromptRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  useEffect(() => {
    let cancelled = false;
    async function poll() {
      // Each backend fails independently — a Python outage shouldn't blank
      // out .NET's row (or vice versa).
      const [pythonResult, dotnetResult] = await Promise.allSettled([
        fetchStatus("python"),
        fetchStatus("dotnet"),
      ]);
      if (cancelled) return;

      const combined = [
        ...(pythonResult.status === "fulfilled" ? pythonResult.value : []),
        ...(dotnetResult.status === "fulfilled" ? dotnetResult.value : []),
      ];

      if (pythonResult.status === "rejected" && dotnetResult.status === "rejected") {
        setError("Failed to load prompt status from both backends");
      } else {
        setError(null);
        setRows(combined);
        setLastUpdated(new Date());
      }
    }
    poll();
    const id = setInterval(poll, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, []);

  if (error && !rows) {
    return (
      <Box borderWidth="1px" borderRadius="lg" p={4} bg="red.50" borderColor="red.200">
        <Text fontSize="sm" color="red.700">{error}</Text>
      </Box>
    );
  }

  if (!rows) {
    return (
      <Box borderWidth="1px" borderRadius="lg" p={4}>
        <HStack gap={2}>
          <Spinner size="xs" />
          <Text fontSize="sm" color="gray.600">Loading prompt versions...</Text>
        </HStack>
      </Box>
    );
  }

  return (
    <Box borderWidth="1px" borderRadius="lg" p={4} bg="white">
      <Flex justify="space-between" align="center" mb={3}>
        <HStack gap={2}>
          <Layers size={16} color="#3b82f6" />
          <Text fontWeight="semibold" fontSize="sm">Active prompt versions</Text>
          <Badge variant="subtle" colorPalette="gray" fontSize="2xs">{rows.length}</Badge>
        </HStack>
        {lastUpdated && (
          <Text fontSize="xs" color="gray.500">Updated {lastUpdated.toLocaleTimeString()}</Text>
        )}
      </Flex>

      {rows.length === 0 ? (
        <Text fontSize="sm" color="gray.500">
          No prompt resolutions recorded yet. Fire a /query on each backend to populate this panel.
        </Text>
      ) : (
        <Box overflowX="auto">
          <Table.Root size="sm" variant="line">
            <Table.Header>
              <Table.Row>
                <Table.ColumnHeader>Subagent (prompt_id)</Table.ColumnHeader>
                <Table.ColumnHeader>Backend</Table.ColumnHeader>
                <Table.ColumnHeader>Version</Table.ColumnHeader>
                <Table.ColumnHeader>Source</Table.ColumnHeader>
                <Table.ColumnHeader>Flag override</Table.ColumnHeader>
              </Table.Row>
            </Table.Header>
            <Table.Body>
              {rows
                .sort((a, b) => a.backend.localeCompare(b.backend) || a.prompt_id.localeCompare(b.prompt_id))
                .map((r) => (
                  <Table.Row key={`${r.backend}:${r.prompt_id}`}>
                    <Table.Cell fontSize="xs" fontWeight="medium">{r.prompt_id}</Table.Cell>
                    <Table.Cell>
                      <Badge variant="subtle" colorPalette={r.backend === "python" ? "blue" : "purple"} fontSize="2xs">
                        {r.backend}
                      </Badge>
                    </Table.Cell>
                    <Table.Cell fontSize="xs" fontFamily="mono">{r.version ?? "—"}</Table.Cell>
                    <Table.Cell>
                      <Badge
                        variant="subtle"
                        fontSize="2xs"
                        colorPalette={r.source === "fallback" ? "orange" : r.source === "flag-pinned" ? "green" : "gray"}
                      >
                        {r.source}
                      </Badge>
                    </Table.Cell>
                    <Table.Cell fontSize="xs" color="gray.500">
                      {r.flag_value > 0 ? `v${r.flag_value}` : "none (default)"}
                    </Table.Cell>
                  </Table.Row>
                ))}
            </Table.Body>
          </Table.Root>
        </Box>
      )}
    </Box>
  );
}
