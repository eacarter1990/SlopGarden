import { Alert, Badge, Button, Card, Grid, Group, NumberInput, Select, Stack, Table, Text, TextInput } from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconAlertTriangle, IconPlus } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { DeviceDetail, DevicePlugin, DiscoveredHardware, PluginComponent } from '../api/types'
import { useAuth } from '../auth'
import { ActuatorControl } from '../components/ActuatorControl'
import { EmergencyStopButton } from '../components/EmergencyStopButton'
import { useSave } from '../components/Forms'
import { PageTitle, ReadingPill, SchemaForm, TargetPicker, TrustBadge, type TargetValue } from '../components/Widgets'
import { ago, metricLabels, titleCase, when } from '../format'
import { usePageEntity } from '../pageContext'

const actuatorKinds = ['pump', 'valve', 'light', 'heater', 'fan', 'fertilizer_doser', 'mister', 'other']

function AdoptForm({ h }: { h: DiscoveredHardware }) {
  const save = useSave()
  const [name, setName] = useState(h.suggestedName)
  const [target, setTarget] = useState<TargetValue>({ targetType: 'garden' })
  return (
    <Stack>
      <Text size="sm" c="dimmed">{h.pluginId} · {h.address}</Text>
      <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} />
      <TargetPicker value={target} onChange={setTarget} />
      <Group justify="flex-end">
        <Button onClick={async () => { await save('adopt_discovered_hardware', { discoveredId: h.id, name, ...target }, `${titleCase(h.kind)} added`); modals.closeAll() }}>Add {h.kind}</Button>
      </Group>
    </Stack>
  )
}

/** Manual configuration of any component a plugin on this device provides, using its settings schema. */
function ComponentForm({ deviceId, plugins }: { deviceId: string; plugins: DevicePlugin[] }) {
  const save = useSave()
  const components = plugins.flatMap((p) => (p.components ?? []).map((c) => ({ ...c, plugin: p })))
  const [componentId, setComponentId] = useState<string | null>(components[0]?.id ?? null)
  const comp = components.find((c) => c.id === componentId) as (PluginComponent & { plugin: DevicePlugin }) | undefined
  const [name, setName] = useState('')
  const [settings, setSettings] = useState<Record<string, unknown>>({})
  const [target, setTarget] = useState<TargetValue>({ targetType: 'garden' })
  const [metric, setMetric] = useState<string | null>('soil_moisture_pct')
  const [channelKey, setChannelKey] = useState('value')
  const [kind, setKind] = useState<string | null>('valve')
  const [interval, setIntervalSec] = useState<number | string>(30)
  if (components.length === 0) return <Text size="sm">This device hasn't reported any plugin components yet.</Text>
  const submit = async () => {
    if (!comp) return
    if (comp.kind === 'sensor') await save('create_sensor', { deviceId, name, pluginId: comp.id, settings, ...target, readIntervalSec: Number(interval), channels: [{ key: channelKey, metric }] }, 'Sensor added')
    else if (comp.kind === 'actuator') await save('create_actuator', { deviceId, name, pluginId: comp.id, settings, ...target, kind }, 'Actuator added')
    else await save('create_camera', { deviceId, name, pluginId: comp.id, settings, ...target }, 'Camera added')
    modals.closeAll()
  }
  return (
    <Stack>
      <Select label="Plugin component" data={components.map((c) => ({ value: c.id, label: `${c.displayName} (${c.kind}) — ${c.plugin.displayName}` }))} value={componentId} onChange={(v) => { setComponentId(v); setSettings({}) }} />
      {comp && comp.plugin.trust === 'unsigned' && <Alert color="orange" icon={<IconAlertTriangle />}>This plugin is unsigned. Its security can't be guaranteed.</Alert>}
      {comp && <Text size="xs" c="dimmed">{comp.description}</Text>}
      <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} required />
      <TargetPicker value={target} onChange={setTarget} />
      {comp?.kind === 'sensor' && (
        <Group grow>
          <TextInput label="Channel key" value={channelKey} onChange={(e) => setChannelKey(e.currentTarget.value)} />
          <Select label="Metric" searchable data={Object.entries(metricLabels).map(([value, label]) => ({ value, label }))} value={metric} onChange={setMetric} />
          <NumberInput label="Interval (s)" value={interval} onChange={setIntervalSec} min={1} />
        </Group>
      )}
      {comp?.kind === 'actuator' && <Select label="Actuator kind" data={(comp.actuatorKinds?.length ? comp.actuatorKinds : actuatorKinds).map((k) => ({ value: k, label: titleCase(k) }))} value={kind} onChange={setKind} />}
      <Text fw={600} size="sm">Plugin settings</Text>
      <SchemaForm schema={comp?.settingsSchema} value={settings} onChange={setSettings} />
      <Group justify="flex-end"><Button disabled={!name} onClick={submit}>Add</Button></Group>
    </Stack>
  )
}

export function DeviceDetailPage() {
  const { id } = useParams()
  const { canWrite, isAdmin } = useAuth()
  const save = useSave()
  const { data: d } = useQuery({ queryKey: ['device', id], queryFn: () => api.op<DeviceDetail>('get_device', { deviceId: id }), refetchInterval: 15_000 })
  usePageEntity('device', id, d ? `Device: ${d.device.name}` : undefined)
  if (!d) return null
  const dev = d.device
  const unadopted = d.discovered.filter((h) => !h.adoptedComponentId)
  return (
    <Stack gap="lg">
      <PageTitle
        title={dev.name}
        subtitle={`${dev.hostname} · ${dev.os} ${dev.architecture} · client v${dev.clientVersion} · ${dev.ipAddress ?? ''}`}
        right={
          <>
            <Badge size="lg" color={dev.isOnline ? 'green' : 'gray'}>{dev.isOnline ? 'online' : `offline · ${ago(dev.lastSeenAt)}`}</Badge>
            {canWrite && <Button leftSection={<IconPlus size={16} />} variant="light" onClick={() => modals.open({ title: 'Add a sensor, actuator or camera', size: 'lg', children: <ComponentForm deviceId={dev.id} plugins={d.plugins} /> })}>Add component</Button>}
            <EmergencyStopButton scopeType="device" scopeId={dev.id} label="Stop device" />
            {isAdmin && dev.status === 'approved' && <Button color="red" variant="subtle" onClick={() => modals.openConfirmModal({ title: 'Revoke this device?', children: <Text size="sm">Its credential stops working; it must be approved again.</Text>, labels: { confirm: 'Revoke', cancel: 'Cancel' }, confirmProps: { color: 'red' }, onConfirm: () => save('revoke_device', { deviceId: dev.id }, 'Revoked') })}>Revoke</Button>}
          </>
        }
      />
      {dev.configErrors.length > 0 && <Alert color="red" title="The device reported configuration errors">{dev.configErrors.join(' · ')}</Alert>}
      <Grid>
        <Grid.Col span={{ base: 12, lg: 7 }}>
          <Stack>
            {unadopted.length > 0 && (
              <Card style={{ borderColor: 'var(--mantine-color-teal-4)' }}>
                <Text fw={650} mb={4}>Discovered hardware ({unadopted.length})</Text>
                <Text size="xs" c="dimmed" mb="xs">Found by the device's plugins. Add them and choose which bed or plant they belong to — or ask the assistant to set them up.</Text>
                <Table fz="sm">
                  <Table.Tbody>
                    {unadopted.map((h) => (
                      <Table.Tr key={h.id}>
                        <Table.Td><Text size="sm" fw={600}>{h.suggestedName}</Text><Text size="xs" c="dimmed">{h.pluginId} · {h.address}</Text></Table.Td>
                        <Table.Td><Badge variant="light">{h.kind}{h.actuatorKind ? ` · ${h.actuatorKind}` : ''}</Badge></Table.Td>
                        <Table.Td>{h.channels.map((c) => c.metric).join(', ')}</Table.Td>
                        <Table.Td>{canWrite && <Button size="xs" variant="light" onClick={() => modals.open({ title: `Add ${h.suggestedName}`, children: <AdoptForm h={h} /> })}>Add</Button>}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Card>
            )}
            <Card>
              <Text fw={650} mb="xs">Sensors ({d.sensors.length})</Text>
              <Stack gap="xs">
                {d.sensors.map((s) => (
                  <Group key={s.id} justify="space-between">
                    <div><Text size="sm" fw={600}>{s.name}</Text><Text size="xs" c="dimmed">{s.pluginId} · {s.targetType} · every {s.readIntervalSec}s</Text></div>
                    <Group gap={6}>{s.channels.map((c) => <ReadingPill key={c.id} r={{ sensorId: s.id, sensorName: s.name, channelId: c.id, metric: c.metric, unit: c.unit, value: c.lastValue, at: c.lastValueAt }} />)}</Group>
                  </Group>
                ))}
              </Stack>
            </Card>
            <Card>
              <Text fw={650} mb="xs">Actuators ({d.actuators.length})</Text>
              <Stack gap={8}>{d.actuators.map((a) => <ActuatorControl key={a.id} a={a} />)}</Stack>
            </Card>
            <Card>
              <Text fw={650} mb="xs">Cameras ({d.cameras.length})</Text>
              {d.cameras.map((c) => <Text key={c.id} size="sm">{c.name} · {c.captureWidth}×{c.captureHeight} every {c.frameIntervalSec}s · last frame {ago(c.lastFrameAt)}</Text>)}
            </Card>
          </Stack>
        </Grid.Col>
        <Grid.Col span={{ base: 12, lg: 5 }}>
          <Stack>
            <Card>
              <Text fw={650} mb="xs">Plugins</Text>
              <Stack gap="xs">
                {d.plugins.map((p) => (
                  <Card key={p.id} padding="xs" radius="md">
                    <Group justify="space-between" wrap="nowrap">
                      <div style={{ minWidth: 0 }}>
                        <Text size="sm" fw={600}>{p.displayName} <Text span size="xs" c="dimmed">v{p.version}</Text></Text>
                        <Text size="xs" c="dimmed" truncate>{p.pluginId} · {p.kinds.join(', ')} · {p.publisher ?? 'unknown publisher'}</Text>
                      </div>
                      <Group gap={4} wrap="nowrap">
                        {p.catalogEntryId && <Badge size="xs" color="green">in catalog</Badge>}
                        <TrustBadge trust={p.trust} />
                      </Group>
                    </Group>
                    {p.trust === 'unsigned' && <Text size="xs" c="orange" mt={4}>⚠ Unsigned plugin — its security can't be guaranteed.</Text>}
                    <Text size="10px" c="dimmed" ff="monospace" truncate>sha256 {p.sha256}</Text>
                  </Card>
                ))}
              </Stack>
            </Card>
            <Card>
              <Text fw={650} mb="xs">Environment reported</Text>
              <Text size="sm">Timezone: {dev.reportedTimezoneId ?? '–'}</Text>
              <Text size="sm">Location: {dev.reportedLatitude != null ? `${dev.reportedLatitude}, ${dev.reportedLongitude}` : 'not configured on the device'}</Text>
              <Text size="sm">Config: applied v{dev.appliedConfigVersion ?? '–'} / desired v{dev.desiredConfigVersion}</Text>
            </Card>
            <Card>
              <Text fw={650} mb="xs">Recent events</Text>
              <Stack gap={4}>
                {d.recentEvents.map((e) => (
                  <Text key={e.id} size="xs"><Badge size="xs" color={e.severity === 'critical' ? 'red' : e.severity === 'warning' ? 'yellow' : 'gray'} mr={4}>{e.category}</Badge>{when(e.time)} — {e.message}</Text>
                ))}
                {d.recentEvents.length === 0 && <Text size="xs" c="dimmed">None.</Text>}
              </Stack>
            </Card>
          </Stack>
        </Grid.Col>
      </Grid>
    </Stack>
  )
}
