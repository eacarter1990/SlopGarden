import { Alert, Anchor, Badge, Button, Card, Group, NumberInput, PasswordInput, Select, Stack, Switch, Table, Text, Textarea, TextInput } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { IconAlertTriangle, IconCheck, IconWifi, IconX } from '@tabler/icons-react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { Device } from '../api/types'
import { useAuth } from '../auth'
import { useSave } from '../components/Forms'
import { PageTitle } from '../components/Widgets'
import { ago } from '../format'

function splitImage(image: string) {
  const at = image.lastIndexOf(':')
  const slash = image.lastIndexOf('/')
  if (at <= slash) return { repo: image, version: '—' }
  return { repo: image.slice(0, at), version: image.slice(at + 1) }
}

export function DevicesPage() {
  const { isAdmin } = useAuth()
  const save = useSave()
  const qc = useQueryClient()
  const { data: devices } = useQuery({ queryKey: ['devices'], queryFn: () => api.op<Device[]>('list_devices'), refetchInterval: 10_000 })
  const { data: releases } = useQuery({
    queryKey: ['releases'],
    queryFn: () => api.get<{ mode: string; version: string; serverImage: string; clientImage: string; serverAutoUpdate: boolean; running: string; canUpdateServer: boolean; error?: string | null; releases: { version: string; tag: string }[] }>('/api/releases'),
  })
  const [address, setAddress] = useState('')
  const [sshHost, setSshHost] = useState('')
  const [sshPort, setSshPort] = useState<number | string>(22)
  const [sshUser, setSshUser] = useState('')
  const [sshPassword, setSshPassword] = useState('')
  const [sshKey, setSshKey] = useState('')
  const [sshName, setSshName] = useState('')
  const [probe, setProbe] = useState<{ sessionId: string; fingerprint: string; dockerInstalled: boolean; serverUrl: string; detail: string } | null>(null)
  const [sshLog, setSshLog] = useState<string | null>(null)
  const [sshBusy, setSshBusy] = useState(false)
  const pending = (devices ?? []).filter((d) => d.status === 'pending')
  const others = (devices ?? []).filter((d) => d.status !== 'pending')

  const remove = (d: Device) => modals.openConfirmModal({
    title: `Remove ${d.name || d.hostname}?`,
    children: <Text size="sm">This deletes the device and its sensors, cameras, actuators, and their readings. Plants and beds stay. If the client is still running, it shows up again as pending and needs approval.</Text>,
    labels: { confirm: 'Remove device', cancel: 'Cancel' },
    confirmProps: { color: 'red' },
    onConfirm: () => save('remove_device', { deviceId: d.id }, 'Device removed'),
  })

  const addByIp = async () => {
    try {
      const r = await api.op<{ contacted: boolean; message: string }>('add_device_by_ip', { address })
      notifications.show({ color: r.contacted ? 'green' : 'orange', message: r.message })
      setTimeout(() => qc.invalidateQueries({ queryKey: ['devices'] }), 3000)
    } catch (e) {
      notifications.show({ color: 'red', message: (e as Error).message })
    }
  }

  const roster = (
    <>
      {pending.length > 0 && (
        <Card style={{ borderColor: 'rgba(210, 74, 36, 0.45)' }}>
          <Text className="sg-serif" fz={22} fw={560} mb="xs">Waiting for approval ({pending.length})</Text>
          <div className="sg-stack">
            {pending.map((d) => (
              <div key={d.id} className="sg-record sg-record-wide">
                <div>
                  <Text fw={650}>{d.hostname}</Text>
                  <Text size="xs" c="dimmed">{d.os} {d.architecture} · client {d.clientVersion} · via {d.discoveredVia} · {d.ipAddress}</Text>
                  <Text size="xs" c="dimmed">Found {ago(d.createdAt)}</Text>
                </div>
                <div>
                  {d.unsignedPlugins > 0 && <Badge color="orange" variant="light" leftSection={<IconAlertTriangle size={12} />}>{d.unsignedPlugins} unsigned plugin(s)</Badge>}
                </div>
                {isAdmin ? (
                  <Group gap="xs">
                    <Button size="xs" leftSection={<IconCheck size={14} />} onClick={() => save('approve_device', { deviceId: d.id }, 'Device approved; credential issued')}>Approve</Button>
                    <Button size="xs" variant="light" color="gray" leftSection={<IconX size={14} />} onClick={() => save('reject_device', { deviceId: d.id }, 'Rejected')}>Reject</Button>
                    <Button size="xs" variant="subtle" color="red" onClick={() => remove(d)}>Remove</Button>
                  </Group>
                ) : (
                  <Text size="xs" c="dimmed">An Admin must approve</Text>
                )}
              </div>
            ))}
          </div>
        </Card>
      )}
      <Card>
        <Text className="sg-serif" fz={22} fw={560} mb="sm">On this network</Text>
        <div className="sg-stack">
          {others.map((d) => (
            <div key={d.id} className="sg-record sg-record-wide">
              <div>
                <Anchor component={Link} to={`/devices/${d.id}`} fw={650} c="inherit">{d.name}</Anchor>
                <Text size="xs" c="dimmed">{d.ipAddress} · {d.os} {d.architecture} · v{d.clientVersion}</Text>
                <Text size="xs" c="dimmed">Last seen {ago(d.lastSeenAt)}{d.bufferedReadings > 0 ? ` · ${d.bufferedReadings} buffered` : ''}</Text>
              </div>
              <Group gap={6}>
                <Badge color={d.isOnline ? 'green' : 'gray'} variant={d.isOnline ? 'filled' : 'light'}>{d.isOnline ? 'online' : 'offline'}</Badge>
                {d.status !== 'approved' && <Badge color="red" variant="light">{d.status}</Badge>}
                <Badge variant="light" color={d.appliedConfigVersion === d.desiredConfigVersion ? 'green' : 'yellow'}>config v{d.appliedConfigVersion ?? '–'} / v{d.desiredConfigVersion}</Badge>
                {d.canSelfUpdate === false ? <Badge color="gray" variant="light">not a container</Badge> : d.updateAvailable ? <Badge color="yellow">update available</Badge> : <Badge color="gray" variant="light">{d.updateStatus || 'current'}</Badge>}
                {d.configErrors.length > 0 && <Text size="xs" c="red">{d.configErrors.length} error(s)</Text>}
                {d.canSelfUpdate !== false && d.updateError && <Text size="xs" c="red">{d.updateError}</Text>}
              </Group>
              <Stack gap={6}>
                {d.canSelfUpdate === false && <Text size="xs" c="dimmed">This device is a program on its machine, not a container, so an image cannot be installed from here.</Text>}
                {isAdmin && d.status === 'approved' && d.canSelfUpdate !== false && (
                  <Group gap="xs">
                    <Button size="xs" variant="light" disabled={!d.updateAvailable && !releases?.version} onClick={async () => {
                      try {
                        const r = await api.post<{ message: string }>(`/api/devices/${d.id}/update`)
                        notifications.show({ color: 'green', message: r.message })
                        qc.invalidateQueries({ queryKey: ['devices'] })
                      } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
                    }}>Update</Button>
                    <Switch size="xs" label="Auto" aria-label={`Auto-update ${d.name}`} checked={!!d.autoUpdate} onChange={async (e) => {
                      try {
                        await api.put(`/api/devices/${d.id}/auto-update`, { enabled: e.currentTarget.checked })
                        qc.invalidateQueries({ queryKey: ['devices'] })
                      } catch (err) { notifications.show({ color: 'red', message: (err as Error).message }) }
                    }} />
                  </Group>
                )}
                {isAdmin && <Button size="xs" variant="subtle" color="red" onClick={() => remove(d)}>Remove</Button>}
              </Stack>
            </div>
          ))}
        </div>
        {devices?.length === 0 && <Alert mt="sm">No devices yet. Start the SlopGarden client on a Pi (or the emulator-backed client) on the same network.</Alert>}
      </Card>
    </>
  )

  return (
    <Stack>
      <PageTitle title="Devices" subtitle="SlopGarden clients on your network. New clients are found by UDP broadcast, mDNS, or by IP, and stay pending until an Admin approves them." />
      {roster}

      {isAdmin && (
        <Card>
          <Text fw={650} mb={4}>Release</Text>
          <Text size="xs" c="dimmed" mb="xs">
            Updates install the server and client images from a GitHub release. Latest follows the newest release. Pick a version to stay on that one.
            Auto-update does not change safety limits or AI approvals.
          </Text>
          <Group align="flex-end">
            <Select
              label="Version"
              data={[{ value: 'latest', label: 'Latest release' }, ...(releases?.releases ?? []).map((r) => ({ value: r.version, label: r.tag }))]}
              value={releases?.mode === 'pinned' ? releases.version : 'latest'}
              onChange={async (value) => {
                if (!value) return
                try {
                  await api.put('/api/releases', { mode: value === 'latest' ? 'latest' : 'pinned', version: value === 'latest' ? '' : value, serverAutoUpdate: releases?.serverAutoUpdate ?? false })
                  qc.invalidateQueries({ queryKey: ['releases'] })
                  qc.invalidateQueries({ queryKey: ['devices'] })
                } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
              }}
              allowDeselect={false}
              w={240}
            />
            <Switch
              label="Auto-update server"
              checked={!!releases?.serverAutoUpdate}
              onChange={async (e) => {
                try {
                  await api.put('/api/releases', { mode: releases?.mode ?? 'latest', version: releases?.mode === 'pinned' ? releases.version : '', serverAutoUpdate: e.currentTarget.checked })
                  qc.invalidateQueries({ queryKey: ['releases'] })
                } catch (err) { notifications.show({ color: 'red', message: (err as Error).message }) }
              }}
            />
            <Button variant="light" disabled={!releases?.version} onClick={async () => {
              try {
                const r = await api.post<{ message: string }>('/api/server/update')
                notifications.show({ color: 'green', message: r.message })
              } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
            }}>Update server</Button>
          </Group>
          {releases?.version ? (
            <Table mt="sm" fz="sm" verticalSpacing={6} horizontalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Part</Table.Th>
                  <Table.Th>Running</Table.Th>
                  <Table.Th>Release</Table.Th>
                  <Table.Th>Image</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {([
                  ['Server', releases.running || '—', releases.serverImage],
                  ['Client', [...new Set((devices ?? []).map((d) => d.clientVersion).filter(Boolean))].join(', ') || '—', releases.clientImage],
                ] as const).map(([part, running, image]) => {
                  const { repo, version } = splitImage(image)
                  return (
                    <Table.Tr key={part}>
                      <Table.Td data-label="Part" fw={650}>{part}</Table.Td>
                      <Table.Td data-label="Running">{running}</Table.Td>
                      <Table.Td data-label="Release" fw={650}>{version}</Table.Td>
                      <Table.Td data-label="Image" ff="monospace" fz="xs" style={{ overflowWrap: 'anywhere' }}>{repo}</Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          ) : (
            <Text size="xs" c={releases?.error ? 'orange' : 'dimmed'} mt="xs">{releases?.error || 'No GitHub releases published yet.'}</Text>
          )}
          {releases?.version && releases.error && <Text size="xs" c="orange" mt={4}>{releases.error}</Text>}
        </Card>
      )}

      {isAdmin && (
        <Card>
          <Text fw={650} mb={4}>Set up a machine by SSH</Text>
          <Text size="xs" c="dimmed" mb="xs">
            Installs only the client on that machine and points it at this server. You choose a display name, then confirm the host key. The login is forgotten after the install, and the device stays pending until you approve it. The AI cannot see this login.
          </Text>
          <div className="sg-form">
          <TextInput label="Display name" placeholder="Greenhouse Pi" value={sshName} onChange={(e) => setSshName(e.currentTarget.value)} />
          <TextInput label="Host" placeholder="192.168.1.50" value={sshHost} onChange={(e) => setSshHost(e.currentTarget.value)} />
          <NumberInput label="Port" value={sshPort} onChange={setSshPort} min={1} max={65535} />
          <TextInput label="Username" placeholder="pi" value={sshUser} onChange={(e) => setSshUser(e.currentTarget.value)} />
          <PasswordInput label="Password" value={sshPassword} onChange={(e) => setSshPassword(e.currentTarget.value)} />
          <Textarea label="Private key" description="Optional. Paste a PEM key instead of a password." minRows={2} value={sshKey} onChange={(e) => setSshKey(e.currentTarget.value)} />
          <div className="sg-form-actions">
            <Button loading={sshBusy} disabled={!sshHost || !sshUser || (!sshPassword && !sshKey)} onClick={async () => {
              setSshBusy(true)
              setProbe(null)
              setSshLog(null)
              try {
                const r = await api.post<{ sessionId: string; fingerprint: string; dockerInstalled: boolean; serverUrl: string; detail: string }>('/api/devices/remote-setup/probe', {
                  host: sshHost, port: Number(sshPort) || 22, username: sshUser, password: sshPassword || null, privateKey: sshKey || null,
                })
                setProbe(r)
                setSshPassword('')
                setSshKey('')
              } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
              finally { setSshBusy(false) }
            }}>Check host</Button>
            <Button variant="light" disabled={!probe?.dockerInstalled || !sshName.trim() || sshBusy} onClick={async () => {
              if (!probe) return
              setSshBusy(true)
              try {
                const r = await api.post<{ log: string }>(`/api/devices/remote-setup/${probe.sessionId}/install`, { fingerprint: probe.fingerprint, name: sshName, image: releases?.clientImage || null })
                setSshLog(r.log)
                setProbe(null)
                setTimeout(() => qc.invalidateQueries({ queryKey: ['devices'] }), 4000)
              } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
              finally { setSshBusy(false) }
            }}>Install client</Button>
          </div>
          </div>
          {probe && (
            <Alert mt="sm" color={probe.dockerInstalled ? 'green' : 'orange'} title={probe.fingerprint}>
              {probe.detail} This client will be named {sshName || '…'} and will register with {probe.serverUrl}. Confirm this host key, then install.
            </Alert>
          )}
          {sshLog && <Alert mt="sm" color="green" title="Installed">{sshLog} Approve the device when it appears in the list above.</Alert>}
        </Card>
      )}

      {isAdmin && (
        <Card>
          <Text fw={650} mb={4}>Add a device by IP</Text>
          <Text size="xs" c="dimmed" mb="xs">The server tells the client at this address to register here (client local API, port 8787).</Text>
          <Group>
            <TextInput placeholder="192.168.1.42" value={address} onChange={(e) => setAddress(e.currentTarget.value)} leftSection={<IconWifi size={16} />} />
            <Button disabled={!address} onClick={addByIp}>Contact device</Button>
          </Group>
        </Card>
      )}
    </Stack>
  )
}
