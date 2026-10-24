import { Badge, Button, Card, Group, NumberInput, Stack, Stepper, Switch, Table, Text } from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconAdjustments } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Bed, Channel, Plant, Sensor } from '../api/types'
import { useAuth } from '../auth'
import { useSave } from '../components/Forms'
import { ReadingsChart } from '../components/ReadingsChart'
import { PageTitle, ReadingPill } from '../components/Widgets'
import { ago, fmtValue, metricLabel } from '../format'

/** Guided two-point calibration: capture the raw value dry, then wet, map to 0–100 %. */
function CalibrationWizard({ sensor, channel }: { sensor: Sensor; channel: Channel }) {
  const save = useSave()
  const [step, setStep] = useState(0)
  const [dry, setDry] = useState<number | string>(channel.calibration?.rawLow ?? '')
  const [wet, setWet] = useState<number | string>(channel.calibration?.rawHigh ?? '')
  const { data: live } = useQuery({ queryKey: ['sensors', 'cal', sensor.id], queryFn: () => api.op<Sensor[]>('list_sensors', { deviceId: sensor.deviceId }), refetchInterval: 2000 })
  const raw = live?.find((s) => s.id === sensor.id)?.channels.find((c) => c.id === channel.id)?.lastRawValue ?? null
  return (
    <Stack>
      <Text size="sm">Current raw value: <b>{raw ?? '–'}</b></Text>
      <Stepper active={step} onStepClick={setStep} size="sm">
        <Stepper.Step label="Dry" description="Probe in air / dry soil">
          <Group align="flex-end" mt="sm">
            <NumberInput label="Raw value when dry (→ 0 %)" value={dry} onChange={setDry} />
            <Button variant="light" onClick={() => raw !== null && setDry(raw)}>Use current</Button>
          </Group>
        </Stepper.Step>
        <Stepper.Step label="Wet" description="Probe in water / saturated soil">
          <Group align="flex-end" mt="sm">
            <NumberInput label="Raw value when wet (→ 100 %)" value={wet} onChange={setWet} />
            <Button variant="light" onClick={() => raw !== null && setWet(raw)}>Use current</Button>
          </Group>
        </Stepper.Step>
        <Stepper.Completed>
          <Text size="sm" mt="sm">Maps raw {dry} → 0 % and {wet} → 100 % (clamped). The device applies it immediately.</Text>
        </Stepper.Completed>
      </Stepper>
      <Group justify="space-between">
        <Button variant="default" disabled={step === 0} onClick={() => setStep((s) => s - 1)}>Back</Button>
        {step < 2 ? (
          <Button onClick={() => setStep((s) => s + 1)}>Next</Button>
        ) : (
          <Button
            onClick={async () => {
              await save('calibrate_channel', { channelId: channel.id, calibration: { type: 'two_point', rawLow: Number(dry), valueLow: 0, rawHigh: Number(wet), valueHigh: 100, clamp: true } }, 'Calibration saved')
              modals.closeAll()
            }}
          >
            Save calibration
          </Button>
        )}
      </Group>
    </Stack>
  )
}

function SensorRow({ s, targetLabel }: { s: Sensor; targetLabel: string }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [open, setOpen] = useState(false)
  return (
    <Card>
      <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
        <div style={{ minWidth: 0, flex: '1 1 auto' }}>
          <Group gap={6} wrap="wrap">
            <Text fw={650}>{s.name}</Text>
            <Badge size="xs" variant="light" color={s.status === 'ok' ? 'green' : 'red'}>{s.status}</Badge>
            {!s.enabled && <Badge size="xs" color="gray">disabled</Badge>}
          </Group>
          <Text size="xs" c="dimmed">{s.pluginId} on {s.deviceName} · every {s.readIntervalSec}s · targets {targetLabel}</Text>
          {s.lastError && <Text size="xs" c="red">{s.lastError}</Text>}
        </div>
        <Group gap={6} wrap="wrap" justify="flex-end" style={{ flex: '0 0 auto' }}>
          {s.channels.map((c) => <ReadingPill key={c.id} r={{ sensorId: s.id, sensorName: s.name, channelId: c.id, metric: c.metric, unit: c.unit, value: c.lastValue, at: c.lastValueAt, alertLow: c.alertLow, alertHigh: c.alertHigh }} />)}
          <Button size="xs" variant="subtle" onClick={() => setOpen((o) => !o)}>{open ? 'Hide' : 'Details'}</Button>
        </Group>
      </Group>
      {open && (
        <Stack mt="sm">
          <ReadingsChart sensorId={s.id} />
          <Table fz="xs">
            <Table.Thead><Table.Tr><Table.Th>Channel</Table.Th><Table.Th>Raw</Table.Th><Table.Th>Calibration</Table.Th><Table.Th>Alert low</Table.Th><Table.Th>Alert high</Table.Th><Table.Th /></Table.Tr></Table.Thead>
            <Table.Tbody>
              {s.channels.map((c) => (
                <Table.Tr key={c.id}>
                  <Table.Td>{metricLabel(c.metric)} <Text span c="dimmed">({c.key})</Text></Table.Td>
                  <Table.Td>{fmtValue(c.lastRawValue)}</Table.Td>
                  <Table.Td><Badge size="xs" variant="light" color={c.calibrationStatus === 'uncalibrated' ? 'gray' : c.calibrationStatus === 'ai_calibrated' ? 'violet' : 'green'}>{c.calibration?.type ?? 'none'} · {c.calibrationStatus}</Badge></Table.Td>
                  <Table.Td><NumberInput size="xs" w={90} disabled={!canWrite} defaultValue={c.alertLow ?? ''} onBlur={(e) => e.currentTarget.value !== String(c.alertLow ?? '') && save('update_sensor_channel', { channelId: c.id, alertLow: Number(e.currentTarget.value) })} /></Table.Td>
                  <Table.Td><NumberInput size="xs" w={90} disabled={!canWrite} defaultValue={c.alertHigh ?? ''} onBlur={(e) => e.currentTarget.value !== String(c.alertHigh ?? '') && save('update_sensor_channel', { channelId: c.id, alertHigh: Number(e.currentTarget.value) })} /></Table.Td>
                  <Table.Td>{canWrite && <Button size="xs" variant="light" leftSection={<IconAdjustments size={14} />} onClick={() => modals.open({ title: `Calibrate ${s.name}`, size: 'lg', children: <CalibrationWizard sensor={s} channel={c} /> })}>Calibrate</Button>}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          {canWrite && (
            <Group>
              <NumberInput size="xs" label="Reading interval (s)" w={160} defaultValue={s.readIntervalSec} min={1} onBlur={(e) => Number(e.currentTarget.value) !== s.readIntervalSec && save('update_sensor', { sensorId: s.id, readIntervalSec: Number(e.currentTarget.value) })} />
              <Switch mt="lg" label="Enabled" checked={s.enabled} onChange={(e) => save('update_sensor', { sensorId: s.id, enabled: e.currentTarget.checked })} />
              <Button mt="lg" size="xs" color="red" variant="subtle" onClick={() => modals.openConfirmModal({ title: `Delete ${s.name}?`, children: <Text size="sm">This deletes the sensor and its reading history.</Text>, labels: { confirm: 'Delete', cancel: 'Cancel' }, confirmProps: { color: 'red' }, onConfirm: () => save('delete_sensor', { sensorId: s.id }, 'Deleted') })}>Delete sensor</Button>
            </Group>
          )}
          <Text size="xs" c="dimmed">Last value {ago(s.channels[0]?.lastValueAt)}</Text>
        </Stack>
      )}
    </Card>
  )
}

export function SensorsPage() {
  const { data: sensors } = useQuery({ queryKey: ['sensors'], queryFn: () => api.op<Sensor[]>('list_sensors'), refetchInterval: 15_000 })
  const { data: beds } = useQuery({ queryKey: ['beds'], queryFn: () => api.op<Bed[]>('list_beds') })
  const { data: plants } = useQuery({ queryKey: ['plants'], queryFn: () => api.op<Plant[]>('list_plants') })
  const label = (s: Sensor) => (s.plantId ? `plant ${plants?.find((p) => p.id === s.plantId)?.name ?? ''}` : s.bedId ? `bed ${beds?.find((b) => b.id === s.bedId)?.name ?? ''}` : 'whole garden')
  return (
    <Stack>
      <PageTitle title="Sensors & readings" subtitle="Every sensor, what it targets, calibration, alert thresholds and history. Add new sensors from a device page." />
      {(sensors ?? []).map((s) => <SensorRow key={s.id} s={s} targetLabel={label(s)} />)}
      {sensors?.length === 0 && <Card><Text c="dimmed">No sensors yet. Approve a device, then adopt its discovered hardware.</Text></Card>}
    </Stack>
  )
}
