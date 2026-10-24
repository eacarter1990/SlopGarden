import { Badge, Button, Card, Grid, Group, NumberInput, Select, SimpleGrid, Stack, Table, Text, TextInput, Timeline } from '@mantine/core'
import { IconPencil } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Actuator, Bed, Camera, PlantProfile, Sensor } from '../api/types'
import { useAuth } from '../auth'
import { ActuatorControl } from '../components/ActuatorControl'
import { EmergencyStopButton } from '../components/EmergencyStopButton'
import { openPlantForm, useSave } from '../components/Forms'
import { useOverview } from '../components/Layout'
import { ReadingsChart } from '../components/ReadingsChart'
import { AiBadge, CameraImage, PageTitle, ReadingPill } from '../components/Widgets'
import { ago, metricLabel, when } from '../format'
import { usePageEntity } from '../pageContext'

interface CareTarget {
  id: string
  metric: string
  minValue?: number | null
  maxValue?: number | null
  idealValue?: number | null
  source: string
  rationale?: string | null
}

interface JournalEntry {
  id: string
  kind: string
  notes: string
  occurredAt: string
  quantity?: number | null
  unit?: string | null
  createdByType: string
}

interface PlantDetail {
  plant: import('../api/types').Plant
  bed: Bed
  profile?: PlantProfile | null
  careTargets: CareTarget[]
  sensors: Sensor[]
  actuators: Actuator[]
  cameras: Camera[]
  recentJournal: JournalEntry[]
}

const targetMetrics = ['soil_moisture_pct', 'air_temperature_c', 'soil_temperature_c', 'air_humidity_pct', 'light_hours', 'ph', 'ec_ms_cm']

function CareTargets({ d }: { d: PlantDetail }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [metric, setMetric] = useState<string | null>('soil_moisture_pct')
  const [min, setMin] = useState<number | string>('')
  const [max, setMax] = useState<number | string>('')
  const p = d.profile
  const profileRows = p
    ? [
        ['soil_moisture_pct', p.soilMoistureMinPct, p.soilMoistureMaxPct],
        ['air_temperature_c', p.airTempMinC, p.airTempMaxC],
        ['light_hours', p.lightHoursMin, p.lightHoursMax],
        ['ph', p.phMin, p.phMax],
      ]
    : []
  return (
    <Card>
      <Text fw={650} mb="xs">Care targets</Text>
      <Table fz="sm" verticalSpacing={4}>
        <Table.Thead><Table.Tr><Table.Th>Metric</Table.Th><Table.Th>Range</Table.Th><Table.Th>Source</Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>
          {d.careTargets.map((t) => (
            <Table.Tr key={t.id}>
              <Table.Td>{metricLabel(t.metric)}</Table.Td>
              <Table.Td fw={600}>{t.minValue ?? '…'} – {t.maxValue ?? '…'}</Table.Td>
              <Table.Td><Badge size="xs" color={t.source === 'ai' ? 'violet' : 'blue'} variant="light">{t.source}</Badge>{t.rationale && <Text size="xs" c="dimmed">{t.rationale}</Text>}</Table.Td>
            </Table.Tr>
          ))}
          {profileRows.filter(([m]) => !d.careTargets.some((t) => t.metric === m)).map(([m, lo, hi]) => (
            <Table.Tr key={m as string}>
              <Table.Td>{metricLabel(m as string)}</Table.Td>
              <Table.Td>{(lo as number) ?? '…'} – {(hi as number) ?? '…'}</Table.Td>
              <Table.Td><Badge size="xs" color="gray" variant="light">profile</Badge></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {canWrite && (
        <Group mt="sm" align="flex-end" gap="xs">
          <Select size="xs" w={170} data={targetMetrics.map((m) => ({ value: m, label: metricLabel(m) }))} value={metric} onChange={setMetric} />
          <NumberInput size="xs" w={80} placeholder="min" value={min} onChange={setMin} />
          <NumberInput size="xs" w={80} placeholder="max" value={max} onChange={setMax} />
          <Button size="xs" onClick={() => save('set_care_target', { scopeType: 'plant', plantId: d.plant.id, metric, minValue: min === '' ? null : min, maxValue: max === '' ? null : max }, 'Target saved')}>Set</Button>
        </Group>
      )}
    </Card>
  )
}

function Journal({ d }: { d: PlantDetail }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [kind, setKind] = useState<string | null>('observation')
  const [notes, setNotes] = useState('')
  const [qty, setQty] = useState<number | string>('')
  return (
    <Card>
      <Text fw={650} mb="xs">Journal</Text>
      {canWrite && (
        <Group gap="xs" align="flex-end" mb="sm">
          <Select size="xs" w={140} data={['observation', 'note', 'harvest', 'fertilized', 'pruned', 'pest', 'disease', 'watered']} value={kind} onChange={setKind} />
          {kind === 'harvest' && <NumberInput size="xs" w={90} placeholder="grams" value={qty} onChange={setQty} />}
          <TextInput size="xs" style={{ flex: 1 }} placeholder="What happened?" value={notes} onChange={(e) => setNotes(e.currentTarget.value)} />
          <Button size="xs" disabled={!notes} onClick={async () => { await save('add_journal_entry', { plantId: d.plant.id, kind, notes, quantity: qty === '' ? null : qty, unit: kind === 'harvest' ? 'g' : null }, 'Logged'); setNotes(''); setQty('') }}>Log</Button>
        </Group>
      )}
      <Timeline bulletSize={14} lineWidth={2}>
        {d.recentJournal.map((j) => (
          <Timeline.Item key={j.id} color={j.createdByType === 'ai' ? 'violet' : 'green'} title={<Text size="sm" fw={600}>{j.kind}{j.quantity ? ` · ${j.quantity} ${j.unit ?? ''}` : ''}</Text>}>
            <Text size="sm">{j.notes}</Text>
            <Text size="xs" c="dimmed">{when(j.occurredAt)} · by {j.createdByType}</Text>
          </Timeline.Item>
        ))}
      </Timeline>
    </Card>
  )
}

export function PlantPage() {
  const { id } = useParams()
  const { canWrite } = useAuth()
  const { data: d } = useQuery({ queryKey: ['plant', id], queryFn: () => api.op<PlantDetail>('get_plant', { plantId: id }) })
  const { data: o } = useOverview()
  usePageEntity('plant', id, d ? `Plant: ${d.plant.name} (${d.bed.name})` : undefined)
  if (!d) return null
  const aiActive = o?.beds.flatMap((b) => b.plants).find((p) => p.plant.id === id)?.aiActive ?? false
  const moistureTarget = d.careTargets.find((t) => t.metric === 'soil_moisture_pct')
  const target = moistureTarget ? { min: moistureTarget.minValue, max: moistureTarget.maxValue } : d.profile ? { min: d.profile.soilMoistureMinPct, max: d.profile.soilMoistureMaxPct } : undefined

  return (
    <Stack gap="lg">
      <PageTitle
        title={d.plant.name}
        subtitle={`${d.profile?.commonName ?? 'No profile'}${d.plant.variety ? ` · ${d.plant.variety}` : ''} · ${d.bed.name} · planted ${d.plant.plantedAt ?? '?'}`}
        right={
          <>
            <AiBadge active={aiActive} />
            <Badge size="lg" variant="light">{d.plant.stage}</Badge>
            {canWrite && <Button variant="light" leftSection={<IconPencil size={16} />} onClick={() => openPlantForm(d.bed.id, d.plant)}>Edit</Button>}
            <EmergencyStopButton scopeType="plant" scopeId={d.plant.id} label="Stop plant" />
          </>
        }
      />
      <Grid>
        <Grid.Col span={{ base: 12, lg: 7 }}>
          <Stack>
            {d.cameras.map((c) => (
              <Card key={c.id} padding={0}>
                <CameraImage cameraId={c.id} width={900} alt={c.name} />
                <Group justify="space-between" p="xs"><Text size="sm" fw={600}>{c.name}</Text><Text size="xs" c="dimmed">frame {ago(c.lastFrameAt)} · every {c.frameIntervalSec}s</Text></Group>
              </Card>
            ))}
            {[...d.sensors].sort((a, b) => (a.targetType === "plant" ? 0 : 1) - (b.targetType === "plant" ? 0 : 1) || a.name.localeCompare(b.name)).map((s) => (
              <Card key={s.id}>
                <Group justify="space-between" mb="xs">
                  <Text fw={600}>{s.name} <Text span size="xs" c="dimmed">({s.targetType === 'bed' ? 'whole bed' : 'this plant'})</Text></Text>
                  <Group gap={6}>{s.channels.map((c) => <ReadingPill key={c.id} r={{ sensorId: s.id, sensorName: s.name, channelId: c.id, metric: c.metric, unit: c.unit, value: c.lastValue, at: c.lastValueAt }} />)}</Group>
                </Group>
                <ReadingsChart sensorId={s.id} target={s.channels.some((c) => c.metric === 'soil_moisture_pct') ? target : undefined} />
              </Card>
            ))}
            {d.sensors.length === 0 && <Card><Text c="dimmed" size="sm">No sensors target this plant or its bed yet.</Text></Card>}
          </Stack>
        </Grid.Col>
        <Grid.Col span={{ base: 12, lg: 5 }}>
          <Stack>
            {d.actuators.length > 0 && (
              <Card>
                <Text fw={650} mb="xs">Actuators</Text>
                <Stack gap={8}>{d.actuators.map((a) => <ActuatorControl key={a.id} a={a} />)}</Stack>
              </Card>
            )}
            <CareTargets d={d} />
            {d.profile && (
              <Card>
                <Text fw={650}>{d.profile.commonName} needs</Text>
                <Text size="sm" c="dimmed" mb="xs">{d.profile.description}</Text>
                <SimpleGrid cols={2} spacing={4}>
                  <Text size="sm">💧 Water: {d.profile.waterNeed}</Text>
                  <Text size="sm">☀ Light: {d.profile.lightHoursMin}–{d.profile.lightHoursMax} h</Text>
                  <Text size="sm">🌡 Air: {d.profile.airTempMinC}–{d.profile.airTempMaxC} °C</Text>
                  <Text size="sm">⏱ Maturity: {d.profile.daysToMaturityMin}–{d.profile.daysToMaturityMax} d</Text>
                </SimpleGrid>
                {d.profile.fertilizerNotes && <Text size="xs" mt="xs">🧪 {d.profile.fertilizerNotes}</Text>}
              </Card>
            )}
            <Journal d={d} />
          </Stack>
        </Grid.Col>
      </Grid>
    </Stack>
  )
}
