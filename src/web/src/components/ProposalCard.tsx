import { Badge, Button, Card, Checkbox, Group, Stack, Table, Text, Textarea, ThemeIcon } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconCheck, IconSparkles, IconX } from '@tabler/icons-react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Proposal, ToolDoc } from '../api/types'
import { useAuth } from '../auth'
import { ago } from '../format'

const statusColor: Record<string, string> = {
  pending: 'violet',
  approved: 'blue',
  executed: 'green',
  rejected: 'gray',
  failed: 'red',
  expired: 'gray',
}

export function useToolDocs() {
  return useQuery({ queryKey: ['tools'], queryFn: () => api.get<ToolDoc[]>('/api/tools'), staleTime: Infinity })
}

function show(v: unknown): string {
  if (v === null || v === undefined || v === '') return '—'
  if (typeof v === 'object') return JSON.stringify(v)
  return String(v)
}

/** The in-chat approval prompt: what (title + field diff) and why (rationale), with approve/reject. */
export function ProposalCard({ proposal, onDecided }: { proposal: Proposal; onDecided?: (p: Proposal) => void }) {
  const { canWrite } = useAuth()
  const qc = useQueryClient()
  const { data: tools } = useToolDocs()
  const routine = (tools?.find((t) => t.name === proposal.toolName)?.routine ?? false) && proposal.allowStanding !== false
  const [note, setNote] = useState('')
  const [always, setAlways] = useState(false)
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null)
  const pending = proposal.status === 'pending'

  const decide = async (approve: boolean) => {
    setBusy(approve ? 'approve' : 'reject')
    try {
      const p = await api.post<Proposal>(`/api/proposals/${proposal.id}/${approve ? 'approve' : 'reject'}`, { note: note || null, allowAlways: always })
      onDecided?.(p)
      qc.invalidateQueries({ queryKey: ['proposals'] })
      qc.invalidateQueries({ queryKey: ['overview'] })
      if (p.status === 'failed') notifications.show({ color: 'red', title: 'Approved, but it failed', message: show((p.result as { error?: string })?.error) })
    } catch (e) {
      notifications.show({ color: 'red', title: 'Could not record decision', message: (e as Error).message })
    } finally {
      setBusy(null)
    }
  }

  return (
    <Card withBorder radius="md" p="sm" style={{ borderColor: pending ? 'var(--mantine-color-violet-4)' : undefined, borderWidth: pending ? 2 : 1 }}>
      <Stack gap={8}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Group gap={8} wrap="nowrap" align="flex-start">
            <ThemeIcon size="md" radius="xl" color="violet" variant="light">
              <IconSparkles size={16} />
            </ThemeIcon>
            <div>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>AI proposes</Text>
              <Text fw={650} size="sm">{proposal.title}</Text>
            </div>
          </Group>
          <Group gap={4} wrap="nowrap">
            <Badge size="sm" variant="light" color={proposal.risk === 'high' ? 'red' : proposal.risk === 'medium' ? 'yellow' : 'teal'}>{proposal.risk} risk</Badge>
            <Badge size="sm" color={statusColor[proposal.status] ?? 'gray'}>{proposal.status}</Badge>
          </Group>
        </Group>

        {proposal.rationale && (
          <Text size="sm">
            <Text span fw={600}>Why: </Text>
            {proposal.rationale}
          </Text>
        )}

        {proposal.changes.length > 0 && (
          <Table withTableBorder withColumnBorders fz="xs" verticalSpacing={4}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Field</Table.Th>
                <Table.Th>Current</Table.Th>
                <Table.Th>Proposed</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {proposal.changes.map((c, i) => (
                <Table.Tr key={i}>
                  <Table.Td ff="monospace">{c.field}</Table.Td>
                  <Table.Td c="dimmed">{show(c.before)}</Table.Td>
                  <Table.Td fw={600}>{show(c.after)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}

        <Text size="xs" c="dimmed">
          Tool <code>{proposal.toolName}</code> · {ago(proposal.createdAt)}
          {proposal.decisionNote ? ` · note: ${proposal.decisionNote}` : ''}
        </Text>

        {pending && canWrite && (
          <>
            <Textarea size="xs" placeholder="Optional note for the AI" autosize minRows={1} value={note} onChange={(e) => setNote(e.currentTarget.value)} />
            {proposal.allowStanding === false && proposal.toolName === 'run_actuator' && <Text size="xs" c="dimmed">Dosing always needs your approval; "always allow" isn't offered for it.</Text>}
            {routine && (
              <Checkbox
                size="xs"
                checked={always}
                onChange={(e) => setAlways(e.currentTarget.checked)}
                label="Always allow this routine action for this target (within the same limits)"
              />
            )}
            <Group gap="xs">
              <Button size="xs" color="green" leftSection={<IconCheck size={14} />} loading={busy === 'approve'} disabled={!!busy} onClick={() => decide(true)}>
                Approve
              </Button>
              <Button size="xs" color="gray" variant="light" leftSection={<IconX size={14} />} loading={busy === 'reject'} disabled={!!busy} onClick={() => decide(false)}>
                Reject
              </Button>
            </Group>
          </>
        )}
        {pending && !canWrite && <Text size="xs" c="dimmed">Waiting for a ReadWrite user or Admin to decide.</Text>}
      </Stack>
    </Card>
  )
}
