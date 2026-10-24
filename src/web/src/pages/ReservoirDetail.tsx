import { Badge, Button, Card, Grid, Group, NumberInput, Select, Stack, Table, Text, Timeline } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { ReservoirStatus, Sensor } from '../api/types'
import { useAuth } from '../auth'
import { EmergencyStopButton } from '../components/EmergencyStopButton'
import { useSave } from '../components/Forms'
import { ReadingsChart } from '../components/ReadingsChart'
import { ReservoirActions, ReservoirActuators, ReservoirCard } from '../components/Reservoir'
import { CameraImage, PageTitle } from '../components/Widgets'
import { metricLabel, when } from '../format'
import { usePageEntity } from '../pageContext'

interface JournalEntry {
  id: string
  kind: string
  notes: string
  occurredAt: string
  quantity?: number | null
  unit?: string | null
  createdByType: string
}

const targetMetrics = ['ec_ms_cm', 'ph', 'water_temperature_c', 'dissolved_oxygen_mg_l', 'water_level_pct']

function Targets({ r }: { r: ReservoirStatus }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [metric, setMetric] = useState<string | null>('ec_ms_cm')
  const [min, setMin] = useState<number | string>('')
  const [max, setMax] = useState<number | string>('')
  return (
    <Card>
      <Text fw={650} mb="xs">Targets</Text>
      <Table fz="sm" verticalSpacing={4}>
        <Table.Tbody>
          {r.targets.map((t) => (
            <Table.Tr key={t.metric}>
              <Table.Td>{metricLabel(t.metric)}</Table.Td>
              <Table.Td fw={600}>{t.min ?? '…'} – {t.max ?? '…'}</Table.Td>
              <Table.Td><Badge size="xs" variant="light" color={t.source === 'ai' ? 'violet' : t.source === 'user' ? 'blue' : 'gray'}>{t.source}</Badge></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {canWrite && (
        <Group mt="sm" align="flex-end" gap="xs">
          <Select size="xs" w={170} data={targetMetrics.map((m) => ({ value: m, label: metricLabel(m) }))} value={metric} onChange={setMetric} />
          <NumberInput size="xs" w={80} placeholder="min" decimalScale={2} value={min} onChange={setMin} />
          <NumberInput size="xs" w={80} placeholder="max" decimalScale={2} value={max} onChange={setMax} />
          <Button size="xs" onClick={() => save('set_care_target', { scopeType: 'reservoir', reservoirId: r.id, metric, minValue: min === '' ? null : min, maxValue: max === '' ? null : max }, 'Target saved')}>Set</Button>
        </Group>
      )}
    </Card>
  )
}

export function ReservoirPage() {
  const { id } = useParams()
  const { data: r } = useQuery({ queryKey: ['reservoirs', id], queryFn: () => api.op<ReservoirStatus>('get_reservoir', { reservoirId: id }), refetchInterval: 10_000 })
  const { data: sensors } = useQuery({ queryKey: ['sensors', 'reservoir', id], queryFn: () => api.op<Sensor[]>('list_sensors', { reservoirId: id }) })
  const { data: cameras } = useQuery({ queryKey: ['cameras', 'reservoir', id], queryFn: () => api.op<{ id: string; name: string }[]>('list_cameras', { reservoirId: id }) })
  const { data: journal } = useQuery({ queryKey: ['journal', 'reservoir', id], queryFn: () => api.op<JournalEntry[]>('list_journal', { reservoirId: id, limit: 30 }) })
  usePageEntity('reservoir', id, r ? `Reservoir: ${r.name}` : undefined)
  if (!r) return null
  return (
    <Stack gap="lg">
      <PageTitle
        title={r.name}
        subtitle={`${r.volumeLiters ?? '?'} L · feeds ${r.beds.map((b) => b.name).join(', ') || 'no beds'} · change every ${r.changeIntervalDays ?? '?'} days · topped up ${r.toppedUpLitersSinceChange} L since last change`}
        right={<EmergencyStopButton scopeType="reservoir" scopeId={r.id} label="Stop reservoir" />}
      />
      <ReservoirActions r={r} />
      <Grid>
        <Grid.Col span={{ base: 12, lg: 7 }}>
          <Stack>
            <ReservoirCard r={r} />
            {(cameras ?? []).map((c) => (
              <Card key={c.id} padding={0}>
                <CameraImage cameraId={c.id} width={900} alt={c.name} />
                <Text size="sm" fw={600} p="xs">{c.name}</Text>
              </Card>
            ))}
            {(sensors ?? []).map((s) => (
              <Card key={s.id}>
                <Text fw={600} mb="xs">{s.name}</Text>
                <ReadingsChart sensorId={s.id} target={r.targets.find((t) => s.channels.some((c) => c.metric === t.metric))} />
              </Card>
            ))}
          </Stack>
        </Grid.Col>
        <Grid.Col span={{ base: 12, lg: 5 }}>
          <Stack>
            <Card>
              <Text fw={650} mb={4}>Pumps & dosers</Text>
              <Text size="xs" c="dimmed" mb="xs">Doses are always proposed by the AI for your approval. Manual doses respect the mixing wait and hourly limits.</Text>
              <ReservoirActuators r={r} />
            </Card>
            <Targets r={r} />
            {r.dosedSinceChange.length > 0 && (
              <Card>
                <Text fw={650} mb="xs">Dosed since last change</Text>
                {r.dosedSinceChange.map((d) => <Text key={d.actuatorId} size="sm">{d.name}: <b>{d.amountMl} ml</b></Text>)}
              </Card>
            )}
            {r.aiContext && <Card><Text fw={650}>Notes for the AI</Text><Text size="sm">{r.aiContext}</Text></Card>}
            <Card>
              <Text fw={650} mb="xs">Journal</Text>
              <Timeline bulletSize={14} lineWidth={2}>
                {(journal ?? []).map((j) => (
                  <Timeline.Item key={j.id} color={j.createdByType === 'ai' ? 'violet' : 'blue'} title={<Text size="sm" fw={600}>{j.kind.replace('_', ' ')}{j.quantity ? ` · ${j.quantity} ${j.unit ?? ''}` : ''}</Text>}>
                    <Text size="sm">{j.notes}</Text>
                    <Text size="xs" c="dimmed">{when(j.occurredAt)} · by {j.createdByType}</Text>
                  </Timeline.Item>
                ))}
              </Timeline>
              {journal?.length === 0 && <Text size="sm" c="dimmed">Nothing logged yet.</Text>}
            </Card>
          </Stack>
        </Grid.Col>
      </Grid>
    </Stack>
  )
}
