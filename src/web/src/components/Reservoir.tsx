import { Alert, Anchor, Badge, Button, Card, Group, NumberInput, Stack, Text, TextInput, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconAlertTriangle, IconDroplets } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { HydroSystemInfo, LatestReading, ReservoirStatus } from '../api/types'
import { useAuth } from '../auth'
import { ActuatorControl } from './ActuatorControl'
import { useSave } from './Forms'
import { AiBadge, ReadingPill } from './Widgets'

export function useHydroSystems() {
  return useQuery({ queryKey: ['hydro-systems'], queryFn: () => api.op<HydroSystemInfo[]>('list_hydro_systems'), staleTime: Infinity })
}

export function useReservoirs() {
  return useQuery({ queryKey: ['reservoirs'], queryFn: () => api.op<ReservoirStatus[]>('list_reservoirs'), refetchInterval: 15_000 })
}

function ReservoirForm({ reservoir }: { reservoir?: ReservoirStatus }) {
  const save = useSave()
  const [name, setName] = useState(reservoir?.name ?? '')
  const [volume, setVolume] = useState<number | string>(reservoir?.volumeLiters ?? 50)
  const [interval, setIntervalDays] = useState<number | string>(reservoir?.changeIntervalDays ?? 14)
  const [aiContext, setAiContext] = useState(reservoir?.aiContext ?? '')
  return (
    <div className="sg-form">
      <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} required data-autofocus />
      <NumberInput label="Volume" description="Litres" min={1} value={volume} onChange={setVolume} />
      <NumberInput label="Change solution" description="Days between changes" min={1} value={interval} onChange={setIntervalDays} />
      <Textarea label="Notes" description="Nutrient brand, A/B ratio, what pH Up and pH Down contain" autosize minRows={2} value={aiContext} onChange={(e) => setAiContext(e.currentTarget.value)} />
      <div className="sg-form-actions">
        <Button
          disabled={!name}
          onClick={async () => {
            const args = { name, volumeLiters: Number(volume), changeIntervalDays: Number(interval), aiContext }
            await save(reservoir ? 'update_reservoir' : 'create_reservoir', reservoir ? { reservoirId: reservoir.id, ...args } : args, 'Reservoir saved')
            modals.closeAll()
          }}
        >
          {reservoir ? 'Save reservoir' : 'Create reservoir'}
        </Button>
      </div>
    </div>
  )
}

export const openReservoirForm = (reservoir?: ReservoirStatus) =>
  modals.open({ title: reservoir ? `Edit ${reservoir.name}` : 'New reservoir', size: 'lg', children: <ReservoirForm reservoir={reservoir} /> })

const order = ['ec_ms_cm', 'ph', 'water_temperature_c', 'water_level_pct', 'flow_l_min', 'dissolved_oxygen_mg_l']

export function reservoirReadings(r: ReservoirStatus): LatestReading[] {
  return [...r.readings].sort((a, b) => (order.indexOf(a.metric) + 99) % 99 - (order.indexOf(b.metric) + 99) % 99)
}

/** Reservoir summary: EC/pH/water temp/level/flow against targets, pumps, last change and alerts. */
export function ReservoirCard({ r, compact }: { r: ReservoirStatus; compact?: boolean }) {
  const pumps = r.actuators.filter((a) => a.role === 'circulation_pump' || a.role === 'air_pump')
  return (
    <Card padding={compact ? 'xs' : 'md'} radius="md" className={r.aiActive ? 'sg-ai-active' : undefined}>
      <Group justify="space-between" wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <IconDroplets size={16} color="var(--sg-moss)" />
          <Anchor component={Link} to={`/reservoirs/${r.id}`} size="sm" fw={650} c="inherit">{r.name}</Anchor>
          {r.volumeLiters ? <Text size="xs" c="dimmed">{r.volumeLiters} L</Text> : null}
        </Group>
        <Group gap={4} wrap="nowrap">
          <AiBadge active={r.aiActive} label="AI" />
          <Badge size="xs" variant="light" color={r.changeDue ? 'orange' : 'gray'}>
            {r.daysSinceChange != null ? `changed ${r.daysSinceChange.toFixed(0)} d ago` : 'never changed'}
          </Badge>
        </Group>
      </Group>
      <Group gap={6} mt={6}>
        {reservoirReadings(r).map((x) => <ReadingPill key={x.channelId} r={x} target={r.targets.find((t) => t.metric === x.metric)} />)}
        {r.readings.length === 0 && <Text size="xs" c="dimmed">No reservoir sensors yet.</Text>}
      </Group>
      {pumps.length > 0 && (
        <Group gap={6} mt={6}>
          {pumps.map((p) => (
            <Badge key={p.id} size="sm" variant="light" color={p.isOn ? 'green' : 'gray'}>{p.name} · {p.isOn ? 'on' : 'off'}</Badge>
          ))}
        </Group>
      )}
      {r.alerts.length > 0 && (
        <Alert mt={6} p={6} color={r.alerts.some((a) => a.includes('flow') || a.includes('continuously')) ? 'red' : 'yellow'} icon={<IconAlertTriangle size={14} />}>
          <Text size="xs">{r.alerts.join(' · ')}</Text>
        </Alert>
      )}
    </Card>
  )
}

/** Logging a solution change or top-up from the reservoir page. */
export function ReservoirActions({ r }: { r: ReservoirStatus }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [liters, setLiters] = useState<number | string>(5)
  if (!canWrite) return null
  return (
    <Group gap="xs" align="flex-end">
      <Button size="xs" variant="light" onClick={() => modals.openConfirmModal({
        title: `Log a full solution change for ${r.name}?`,
        children: <Text size="sm">Resets the change timer and the dosing/top-up totals.</Text>,
        labels: { confirm: 'Log change', cancel: 'Cancel' },
        onConfirm: () => save('log_reservoir_event', { reservoirId: r.id, kind: 'change' }, 'Solution change logged'),
      })}>Log solution change</Button>
      <NumberInput size="xs" w={90} min={0.5} step={0.5} value={liters} onChange={setLiters} suffix=" L" />
      <Button size="xs" variant="light" onClick={() => save('log_reservoir_event', { reservoirId: r.id, kind: 'top_up', liters: Number(liters) }, 'Top-up logged')}>Log top-up</Button>
      <Button size="xs" variant="subtle" onClick={() => openReservoirForm(r)}>Edit</Button>
    </Group>
  )
}

export function ReservoirActuators({ r }: { r: ReservoirStatus }) {
  return <Stack gap={8}>{r.actuators.map((a) => <ActuatorControl key={a.id} a={a} />)}</Stack>
}
