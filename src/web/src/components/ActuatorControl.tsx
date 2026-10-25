import { ActionIcon, Badge, Group, Menu, Text, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconBulb, IconDroplet, IconFlame, IconPlayerPlay, IconPower, IconPropeller, IconTestPipe, IconToggleLeft } from '@tabler/icons-react'
import { api } from '../api/client'
import { useAuth } from '../auth'
import { ago, isDoser } from '../format'

const icons: Record<string, typeof IconDroplet> = {
  pump: IconDroplet,
  valve: IconDroplet,
  mister: IconDroplet,
  light: IconBulb,
  heater: IconFlame,
  fan: IconPropeller,
  fertilizer_doser: IconTestPipe,
}

export function actuatorIcon(kind: string) {
  return icons[kind] ?? IconToggleLeft
}

interface Brief {
  id: string
  name: string
  kind: string
  isOn: boolean
  stateSince?: string | null
  enabled: boolean
  role?: string | null
  continuousRun?: boolean
}

async function run(id: string, action: string, durationSec?: number, amountMl?: number) {
  try {
    const r = await api.op<{ status: string; durationSec?: number; notes: string[] }>('run_actuator', { actuatorId: id, action, durationSec, amountMl, reason: 'Manual control from the web app' })
    notifications.show({ color: 'green', title: `Command ${r.status}`, message: r.notes.length ? r.notes.join(' · ') : `${action}${r.durationSec ? ` for ${Math.round(r.durationSec)} s` : ''}` })
  } catch (e) {
    notifications.show({ color: 'orange', title: 'Blocked', message: (e as Error).message })
  }
}

/** Compact state + manual control for one actuator. Every command goes through the safety limits. */
export function ActuatorControl({ a }: { a: Brief }) {
  const { canWrite } = useAuth()
  const Icon = actuatorIcon(a.kind)
  return (
    <Group justify="space-between" wrap="nowrap" gap="xs" align="center">
      <Group gap={6} wrap="nowrap" style={{ minWidth: 0, flex: '1 1 auto' }}>
        <Icon size={16} style={{ flex: '0 0 auto' }} color={a.isOn ? 'var(--sg-moss)' : 'var(--sg-ink-soft)'} />
        <Text size="sm" style={{ minWidth: 0 }}>{a.name}</Text>
        {a.continuousRun && <Badge size="xs" variant="light" color="gray" style={{ flexShrink: 0 }}>24/7</Badge>}
      </Group>
      <Group gap={4} wrap="nowrap" style={{ flex: '0 0 auto' }}>
        <Tooltip label={a.stateSince ? `since ${ago(a.stateSince)}` : 'no state reported yet'}>
          <Badge size="sm" color={a.isOn ? 'green' : 'gray'} variant={a.isOn ? 'filled' : 'light'} style={{ flexShrink: 0 }}>{a.isOn ? 'On' : 'Off'}</Badge>
        </Tooltip>
        {canWrite && a.enabled && (
          <Menu withinPortal position="bottom-end">
            <Menu.Target>
              <ActionIcon size="sm" variant="light" aria-label={`Control ${a.name}`}><IconPlayerPlay size={14} /></ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              {isDoser(a) ? (
                <>
                  <Menu.Item onClick={() => run(a.id, 'dose', undefined, 2)}>Dose 2 ml</Menu.Item>
                  <Menu.Item onClick={() => run(a.id, 'dose', undefined, 5)}>Dose 5 ml</Menu.Item>
                  <Menu.Item onClick={() => run(a.id, 'dose', undefined, 10)}>Dose 10 ml</Menu.Item>
                </>
              ) : (
                <>
                  <Menu.Item onClick={() => run(a.id, 'pulse', 15)}>Run 15 s</Menu.Item>
                  <Menu.Item onClick={() => run(a.id, 'pulse', 60)}>Run 1 min</Menu.Item>
                  <Menu.Item onClick={() => run(a.id, 'pulse', 300)}>Run 5 min</Menu.Item>
                  <Menu.Item onClick={() => run(a.id, 'on')}>Switch on</Menu.Item>
                </>
              )}
              <Menu.Divider />
              <Menu.Item leftSection={<IconPower size={14} />} color="red" onClick={() => run(a.id, 'off')}>Switch off</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
      </Group>
    </Group>
  )
}
