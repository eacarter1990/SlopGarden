import { Badge, Button, Card, Group, Menu, Stack, Switch, Text } from '@mantine/core'
import { IconBellOff } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { NotificationItem } from '../api/types'
import { useAuth } from '../auth'
import { useSave } from '../components/Forms'
import { Markdown } from '../components/Markdown'
import { PageTitle } from '../components/Widgets'
import { ago } from '../format'

interface Mute {
  id: string
  scopeType: string
  scopeId: string
  category?: string | null
  mutedUntil?: string | null
  reason?: string | null
}

const muteScope: Record<string, string> = { sensor: 'sensor', actuator: 'device', device: 'device', plant: 'plant', bed: 'bed' }

export function NotificationsPage() {
  const { canWrite } = useAuth()
  const save = useSave()
  const [unread, setUnread] = useState(false)
  const { data } = useQuery({ queryKey: ['notifications', unread], queryFn: () => api.op<NotificationItem[]>('list_notifications', { unreadOnly: unread, limit: 200 }) })
  const { data: mutes } = useQuery({ queryKey: ['notifications', 'mutes'], queryFn: () => api.op<Mute[]>('list_notification_mutes') })
  const [perm, setPerm] = useState(typeof Notification !== 'undefined' ? Notification.permission : 'denied')

  return (
    <Stack>
      <PageTitle
        title="Notifications"
        subtitle="Alerts, device events, safety blocks and AI proposals. Mute a plant or device once you know about an issue."
      />
      <Group className="sg-page-actions">
        {perm !== 'granted' && typeof Notification !== 'undefined' && <Button variant="light" onClick={async () => setPerm(await Notification.requestPermission())}>Enable browser notifications</Button>}
        <Switch label="Unread only" checked={unread} onChange={(e) => setUnread(e.currentTarget.checked)} />
        <Button variant="light" onClick={() => save('mark_notifications_read', {}, 'All marked read')}>Mark all read</Button>
      </Group>
      {(mutes ?? []).length > 0 && (
        <Card>
          <Text fw={650} mb="xs">Muted</Text>
          <Group gap="xs">
            {(mutes ?? []).map((m) => (
              <Badge key={m.id} variant="light" color="gray" rightSection={canWrite && <span style={{ cursor: 'pointer' }} onClick={() => save('unmute_notifications', { muteId: m.id }, 'Unmuted')}>✕</span>}>
                {m.scopeType} {m.scopeId.slice(0, 6)}{m.category ? ` · ${m.category}` : ''}{m.mutedUntil ? ` until ${ago(m.mutedUntil)}` : ''}
              </Badge>
            ))}
          </Group>
        </Card>
      )}
      {(data ?? []).map((n) => (
        <Card key={n.id} padding="sm" style={{ opacity: n.readAt ? 0.7 : 1, borderLeft: `4px solid var(--mantine-color-${n.severity === 'critical' ? 'red' : n.severity === 'warning' ? 'yellow' : 'green'}-6)` }}>
          <Group justify="space-between" wrap="nowrap" align="flex-start">
            <div style={{ minWidth: 0 }}>
              <Group gap={6}>
                <Text fw={650} size="sm">{n.title}</Text>
                <Badge size="xs" variant="light">{n.category}</Badge>
              </Group>
              <Markdown>{n.body}</Markdown>
              <Text size="xs" c="dimmed">{ago(n.createdAt)}</Text>
            </div>
            {canWrite && n.targetType && n.targetId && muteScope[n.targetType] && (
              <Menu position="bottom-end">
                <Menu.Target><Button size="xs" variant="subtle" color="gray" leftSection={<IconBellOff size={14} />}>Mute</Button></Menu.Target>
                <Menu.Dropdown>
                  {[1, 24, 168].map((h) => (
                    <Menu.Item key={h} onClick={() => save('mute_notifications', { scopeType: n.targetType === 'actuator' ? 'device' : n.targetType, scopeId: n.targetId, category: n.category, hours: h }, 'Muted')}>
                      {n.category} for this {n.targetType} · {h === 1 ? '1 hour' : h === 24 ? '1 day' : '1 week'}
                    </Menu.Item>
                  ))}
                  <Menu.Item onClick={() => save('mute_notifications', { scopeType: n.targetType, scopeId: n.targetId }, 'Muted')}>Everything for this {n.targetType}, until unmuted</Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
          </Group>
        </Card>
      ))}
      {data?.length === 0 && <div className="sg-empty">{unread ? 'No unread notifications.' : 'No notifications yet.'}</div>}
    </Stack>
  )
}
