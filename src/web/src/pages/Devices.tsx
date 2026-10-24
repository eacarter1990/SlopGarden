import { Alert, Anchor, Badge, Button, Card, Group, Stack, Switch, Table, Text, TextInput } from '@mantine/core'
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

export function DevicesPage() {
  const { isAdmin } = useAuth()
  const save = useSave()
  const qc = useQueryClient()
  const { data: devices } = useQuery({ queryKey: ['devices'], queryFn: () => api.op<Device[]>('list_devices'), refetchInterval: 10_000 })
  const { data: release } = useQuery({ queryKey: ['client-release'], queryFn: () => api.get<{ version: string; image: string }>('/api/client-release') })
  const [address, setAddress] = useState('')
  const [version, setVersion] = useState('')
  const [image, setImage] = useState('')
  const pending = (devices ?? []).filter((d) => d.status === 'pending')
  const others = (devices ?? []).filter((d) => d.status !== 'pending')

  const addByIp = async () => {
    try {
      const r = await api.op<{ contacted: boolean; message: string }>('add_device_by_ip', { address })
      notifications.show({ color: r.contacted ? 'green' : 'orange', message: r.message })
      setTimeout(() => qc.invalidateQueries({ queryKey: ['devices'] }), 3000)
    } catch (e) {
      notifications.show({ color: 'red', message: (e as Error).message })
    }
  }

  return (
    <Stack>
      <PageTitle title="Devices" subtitle="SlopGarden clients on your network. New clients are found by UDP broadcast, mDNS, or by IP, and stay pending until an Admin approves them." />

      {isAdmin && (
        <Card>
          <Text fw={650} mb={4}>Client image</Text>
          <Text size="xs" c="dimmed" mb="xs">
            Published version {release?.version || '—'}{release?.image ? ` · ${release.image}` : ''}. A device running an older image shows an update here.
            Turning on auto-update installs that image. It does not change safety limits or AI approvals.
          </Text>
          <Group align="flex-end">
            <TextInput label="Version" placeholder="0.2.3" value={version} onChange={(e) => setVersion(e.currentTarget.value)} />
            <TextInput label="Image" placeholder="ghcr.io/owner/slopgarden-client:0.2.3" value={image} onChange={(e) => setImage(e.currentTarget.value)} style={{ flex: 1 }} />
            <Button disabled={!version} onClick={async () => {
              try {
                await api.put('/api/client-release', { version, image })
                notifications.show({ color: 'green', message: 'Client release saved' })
                qc.invalidateQueries({ queryKey: ['client-release'] })
                qc.invalidateQueries({ queryKey: ['devices'] })
              } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
            }}>Save release</Button>
          </Group>
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

      {pending.length > 0 && (
        <Card style={{ borderColor: 'var(--mantine-color-orange-4)' }}>
          <Text fw={650} mb="xs">Waiting for approval ({pending.length})</Text>
          <Table>
            <Table.Tbody>
              {pending.map((d) => (
                <Table.Tr key={d.id}>
                  <Table.Td><Text fw={600}>{d.hostname}</Text><Text size="xs" c="dimmed">{d.os} {d.architecture} · client {d.clientVersion} · via {d.discoveredVia} · {d.ipAddress}</Text></Table.Td>
                  <Table.Td>
                    {d.unsignedPlugins > 0 && <Badge color="orange" variant="light" leftSection={<IconAlertTriangle size={12} />}>{d.unsignedPlugins} unsigned plugin(s)</Badge>}
                  </Table.Td>
                  <Table.Td>{ago(d.createdAt)}</Table.Td>
                  <Table.Td>
                    {isAdmin ? (
                      <Group gap="xs" justify="flex-end">
                        <Button size="xs" leftSection={<IconCheck size={14} />} onClick={() => save('approve_device', { deviceId: d.id }, 'Device approved; credential issued')}>Approve</Button>
                        <Button size="xs" variant="light" color="gray" leftSection={<IconX size={14} />} onClick={() => save('reject_device', { deviceId: d.id }, 'Rejected')}>Reject</Button>
                      </Group>
                    ) : (
                      <Text size="xs" c="dimmed">An Admin must approve</Text>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      <Card>
        <Table highlightOnHover>
          <Table.Thead>
            <Table.Tr><Table.Th>Device</Table.Th><Table.Th>Status</Table.Th><Table.Th>Platform</Table.Th><Table.Th>Config</Table.Th><Table.Th>Update</Table.Th><Table.Th>Last seen</Table.Th></Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {others.map((d) => (
              <Table.Tr key={d.id}>
                <Table.Td><Anchor component={Link} to={`/devices/${d.id}`} fw={600}>{d.name}</Anchor><Text size="xs" c="dimmed">{d.ipAddress}</Text></Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    <Badge color={d.isOnline ? 'green' : 'gray'} variant={d.isOnline ? 'filled' : 'light'}>{d.isOnline ? 'online' : 'offline'}</Badge>
                    {d.status !== 'approved' && <Badge color="red" variant="light">{d.status}</Badge>}
                  </Group>
                </Table.Td>
                <Table.Td><Text size="sm">{d.os}</Text><Text size="xs" c="dimmed">{d.architecture} · v{d.clientVersion}</Text></Table.Td>
                <Table.Td>
                  <Badge variant="light" color={d.appliedConfigVersion === d.desiredConfigVersion ? 'green' : 'yellow'}>v{d.appliedConfigVersion ?? '–'} / v{d.desiredConfigVersion}</Badge>
                  {d.configErrors.length > 0 && <Text size="xs" c="red">{d.configErrors.length} error(s)</Text>}
                </Table.Td>
                <Table.Td>
                  <Stack gap={4}>
                    {d.updateAvailable ? <Badge color="yellow">update available</Badge> : <Badge color="gray" variant="light">{d.updateStatus || 'current'}</Badge>}
                    {d.updateError && <Text size="xs" c="red">{d.updateError}</Text>}
                    {isAdmin && d.status === 'approved' && (
                      <Group gap="xs">
                        <Button size="xs" variant="light" disabled={!d.updateAvailable && !release?.version} onClick={async () => {
                          try {
                            const r = await api.post<{ message: string }>(`/api/devices/${d.id}/update`)
                            notifications.show({ color: 'green', message: r.message })
                            qc.invalidateQueries({ queryKey: ['devices'] })
                          } catch (e) { notifications.show({ color: 'red', message: (e as Error).message }) }
                        }}>Update</Button>
                        <Switch
                          size="xs"
                          label="Auto"
                          aria-label={`Auto-update ${d.name}`}
                          checked={!!d.autoUpdate}
                          onChange={async (e) => {
                            try {
                              await api.put(`/api/devices/${d.id}/auto-update`, { enabled: e.currentTarget.checked })
                              qc.invalidateQueries({ queryKey: ['devices'] })
                            } catch (err) { notifications.show({ color: 'red', message: (err as Error).message }) }
                          }}
                        />
                      </Group>
                    )}
                  </Stack>
                </Table.Td>
                <Table.Td>{ago(d.lastSeenAt)}{d.bufferedReadings > 0 && <Text size="xs" c="dimmed">{d.bufferedReadings} buffered</Text>}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {devices?.length === 0 && <Alert mt="sm">No devices yet. Start the SlopGarden client on a Pi (or the emulator-backed client) on the same network.</Alert>}
      </Card>
    </Stack>
  )
}
