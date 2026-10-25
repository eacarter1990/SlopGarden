import { ActionIcon, Alert, Badge, Button, Card, Group, NumberInput, Select, Stack, Switch, Table, Text, TextInput } from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Actuator, Bed, EmergencyStop, Plant, ReservoirStatus, SafetyLimit, Sensor } from '../api/types'
import { useAuth } from '../auth'
import { useSave } from '../components/Forms'
import { useOverview } from '../components/Layout'
import { EmergencyStopButton } from '../components/EmergencyStopButton'
import { PageTitle } from '../components/Widgets'
import { ago, metricLabel, titleCase } from '../format'

const limitTypes: Record<string, { label: string; unit: string; interlock?: boolean }> = {
  max_run_seconds: { label: 'Max watering time per run', unit: 's' },
  max_daily_run_seconds: { label: 'Max watering time per day', unit: 's' },
  min_seconds_between_runs: { label: 'Min time between actions', unit: 's' },
  max_daily_amount_ml: { label: 'Max fertilizer dose per day', unit: 'ml' },
  max_hourly_amount_ml: { label: 'Max dose per hour (per doser)', unit: 'ml' },
  min_seconds_between_doses: { label: 'Mixing wait between doses (per reservoir)', unit: 's' },
  tank_low_lockout: { label: 'No pumping when tank is low', unit: '%', interlock: true },
  max_temperature_cutoff: { label: 'Heater/light cutoff above', unit: '°C', interlock: true },
  min_temperature_cutoff: { label: 'Watering cutoff below', unit: '°C', interlock: true },
}

function useNames() {
  const { data: beds } = useQuery({ queryKey: ['beds'], queryFn: () => api.op<Bed[]>('list_beds') })
  const { data: plants } = useQuery({ queryKey: ['plants'], queryFn: () => api.op<Plant[]>('list_plants') })
  const { data: actuators } = useQuery({ queryKey: ['actuators'], queryFn: () => api.op<Actuator[]>('list_actuators') })
  const { data: sensors } = useQuery({ queryKey: ['sensors'], queryFn: () => api.op<Sensor[]>('list_sensors') })
  const { data: reservoirs } = useQuery({ queryKey: ['reservoirs'], queryFn: () => api.op<ReservoirStatus[]>('list_reservoirs') })
  const name = (type: string, id?: string | null) =>
    !id ? 'everything' : type === 'bed' ? beds?.find((x) => x.id === id)?.name : type === 'plant' ? plants?.find((x) => x.id === id)?.name
      : type === 'actuator' ? actuators?.find((x) => x.id === id)?.name : type === 'reservoir' ? reservoirs?.find((x) => x.id === id)?.name : id.slice(0, 8)
  const channels = (sensors ?? []).flatMap((s) => s.channels.map((c) => ({ value: c.id, label: `${s.name} · ${metricLabel(c.metric)}` })))
  const channelName = (id?: string | null) => channels.find((c) => c.value === id)?.label
  return { beds, plants, actuators, reservoirs, name, channels, channelName }
}

function LimitForm({ gardenId }: { gardenId: string }) {
  const save = useSave()
  const { beds, plants, actuators, reservoirs, channels } = useNames()
  const [limitType, setLimitType] = useState<string | null>('max_run_seconds')
  const [scopeType, setScopeType] = useState<string | null>('global')
  const [scopeId, setScopeId] = useState<string | null>(null)
  const [value, setValue] = useState<number | string>(600)
  const [channelId, setChannelId] = useState<string | null>(null)
  const [kind, setKind] = useState<string | null>(null)
  const [notes, setNotes] = useState('')
  const scopeOptions = scopeType === 'bed' ? beds?.map((b) => ({ value: b.id, label: b.name })) : scopeType === 'plant' ? plants?.map((p) => ({ value: p.id, label: p.name }))
    : scopeType === 'actuator' ? actuators?.map((a) => ({ value: a.id, label: a.name })) : scopeType === 'reservoir' ? reservoirs?.map((r) => ({ value: r.id, label: r.name })) : undefined
  return (
    <Stack>
      <Select label="Limit" data={Object.entries(limitTypes).map(([value, l]) => ({ value, label: l.label }))} value={limitType} onChange={setLimitType} allowDeselect={false} />
      <Group grow>
        <Select label="Applies to" data={['global', 'garden', 'bed', 'plant', 'reservoir', 'actuator'].map((v) => ({ value: v, label: titleCase(v) }))} value={scopeType} onChange={(v) => { setScopeType(v); setScopeId(v === 'garden' ? gardenId : null) }} allowDeselect={false} />
        {scopeOptions && <Select label={titleCase(scopeType ?? '')} data={scopeOptions} value={scopeId} onChange={setScopeId} searchable />}
      </Group>
      <Group grow>
        <NumberInput label={`Value (${limitTypes[limitType ?? '']?.unit})`} value={value} onChange={setValue} />
        <Select label="Only actuator kind (optional)" clearable data={['pump', 'valve', 'light', 'heater', 'fan', 'fertilizer_doser', 'mister']} value={kind} onChange={setKind} />
      </Group>
      {limitTypes[limitType ?? '']?.interlock && <Select label="Watch sensor channel" data={channels} value={channelId} onChange={setChannelId} searchable required />}
      <TextInput label="Notes" value={notes} onChange={(e) => setNotes(e.currentTarget.value)} />
      <Group justify="flex-end">
        <Button onClick={async () => {
          await save('set_safety_limit', { scopeType, scopeId, limitType, value: Number(value), channelId, actuatorKind: kind, notes }, 'Safety limit saved')
          modals.closeAll()
        }}>Save limit</Button>
      </Group>
    </Stack>
  )
}

interface StandingApproval {
  id: string
  toolName: string
  targetType: string
  targetId?: string | null
  constraints: Record<string, unknown>
  createdAt: string
}

export function SafetyPage() {
  const { canWrite } = useAuth()
  const save = useSave()
  const { data: o } = useOverview()
  const { name, channelName } = useNames()
  const { data: limits } = useQuery({ queryKey: ['safety', 'limits'], queryFn: () => api.op<SafetyLimit[]>('list_safety_limits') })
  const { data: stops } = useQuery({ queryKey: ['estops'], queryFn: () => api.op<EmergencyStop[]>('list_emergency_stops') })
  const { data: standing } = useQuery({ queryKey: ['safety', 'standing'], queryFn: () => api.op<StandingApproval[]>('list_standing_approvals') })

  return (
    <Stack>
      <PageTitle
        title="Safety"
        subtitle="Hard limits are enforced on the server and again on every device (even offline). The AI can only propose changes to them."
        right={<>{canWrite && o && <Button leftSection={<IconPlus size={16} />} onClick={() => modals.open({ title: 'New safety limit', size: 'lg', children: <LimitForm gardenId={o.garden.id} /> })}>Add limit</Button>}<EmergencyStopButton scopeType="global" label="Emergency stop everything" /></>}
      />
      <Card style={{ borderColor: (stops ?? []).length ? 'var(--mantine-color-red-5)' : undefined }}>
        <Text fw={650} mb="xs">Emergency stops</Text>
        {(stops ?? []).length === 0 ? (
          <Text size="sm" c="dimmed">None active. Stop buttons exist for the whole system, each garden, bed, plant, device and actuator.</Text>
        ) : (
          <Table>
            <Table.Tbody>
              {(stops ?? []).map((s) => (
                <Table.Tr key={s.id}>
                  <Table.Td><Badge color="red">{s.scopeType}</Badge> {name(s.scopeType, s.scopeId)}</Table.Td>
                  <Table.Td>{s.reason}</Table.Td>
                  <Table.Td><Text size="xs" c="dimmed">by {s.activatedByType} · {ago(s.activatedAt)}</Text></Table.Td>
                  <Table.Td>{canWrite && <Button size="xs" variant="light" onClick={() => save('release_emergency_stop', { stopId: s.id }, 'Released')}>Release</Button>}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Card>
      <Card>
        <Text fw={650} mb="xs">Hard safety limits</Text>
        <Alert variant="light" mb="sm">The most restrictive limit wins for each actuator (global → garden → bed → plant → device → actuator).</Alert>
        <div className="sg-stack">
          {(limits ?? []).map((l) => (
            <div key={l.id} className="sg-record sg-record-wide">
              <div>
                <Text fw={650}>{limitTypes[l.limitType]?.label ?? l.limitType}</Text>
                <Text size="sm" c="dimmed">{titleCase(l.scopeType)}{l.scopeId ? ` · ${name(l.scopeType, l.scopeId)}` : ''}{l.actuatorKind ? ` · ${l.actuatorKind}` : ''}</Text>
                <Text size="xs" c="dimmed">{channelName(l.channelId) ? `Watches ${channelName(l.channelId)}` : 'No sensor watch'} · {l.createdByType}{l.notes ? ` · ${l.notes}` : ''}</Text>
              </div>
              <Text className="sg-serif" fz={26} fw={560}>{l.operator === 'gt' ? '> ' : l.operator === 'lt' ? '< ' : ''}{l.value} <Text span fz="sm" c="dimmed">{limitTypes[l.limitType]?.unit}</Text></Text>
              <Group gap="xs" wrap="nowrap">
                <Switch size="sm" checked={l.enabled} disabled={!canWrite} aria-label={`Enable ${limitTypes[l.limitType]?.label ?? l.limitType}`} onChange={(e) => save('set_safety_limit', { limitId: l.id, enabled: e.currentTarget.checked })} />
                {canWrite && <ActionIcon variant="subtle" color="red" aria-label="Remove limit" onClick={() => save('delete_safety_limit', { limitId: l.id }, 'Removed')}><IconTrash size={16} /></ActionIcon>}
              </Group>
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <Text fw={650} mb="xs">Standing approvals ("always allow")</Text>
        <Text size="sm" c="dimmed" mb="xs">Routine actions you allowed the AI to repeat without asking, within the recorded limits.</Text>
        {(standing ?? []).length === 0 ? <Text size="sm" c="dimmed">None.</Text> : (
          <Table>
            <Table.Tbody>
              {(standing ?? []).map((s) => (
                <Table.Tr key={s.id}>
                  <Table.Td ff="monospace" fz="sm">{s.toolName}</Table.Td>
                  <Table.Td>{s.targetType} {name(s.targetType, s.targetId)}</Table.Td>
                  <Table.Td fz="xs" ff="monospace">{JSON.stringify(s.constraints)}</Table.Td>
                  <Table.Td>{ago(s.createdAt)}</Table.Td>
                  <Table.Td>{canWrite && <Button size="xs" variant="subtle" color="red" onClick={async () => { await api.del(`/api/standing-approvals/${s.id}`); save('list_standing_approvals', {}, 'Revoked') }}>Revoke</Button>}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Card>
    </Stack>
  )
}
