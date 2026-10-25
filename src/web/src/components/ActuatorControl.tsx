import { notifications } from '@mantine/notifications'
import { IconBulb, IconDroplet, IconFlame, IconPower, IconPropeller, IconTestPipe, IconToggleLeft } from '@tabler/icons-react'
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

/** Name and live state only. Used where the person is looking, not operating. */
export function ActuatorStatus({ a }: { a: Brief }) {
  const Icon = actuatorIcon(a.kind)
  const since = a.stateSince ? `since ${ago(a.stateSince)}` : 'no state reported yet'
  return (
    <div className="sg-actuator-status">
      <Icon size={16} color={a.isOn ? 'var(--sg-moss)' : 'var(--sg-ink-soft)'} />
      <span className="sg-actuator-name">{a.name}</span>
      {a.continuousRun && <span className="sg-actuator-note">Always on</span>}
      <span className={a.isOn ? 'sg-actuator-state is-on' : 'sg-actuator-state'} title={since}>
        <span className="sg-dot" />
        {a.isOn ? 'On' : 'Off'}
      </span>
    </div>
  )
}

/** Status plus the run choices. Every command still goes through the safety limits. */
export function ActuatorControl({ a }: { a: Brief }) {
  const { canWrite } = useAuth()
  const doses = isDoser(a)
  const actions = doses
    ? [
        { label: '2 ml', go: () => run(a.id, 'dose', undefined, 2) },
        { label: '5 ml', go: () => run(a.id, 'dose', undefined, 5) },
        { label: '10 ml', go: () => run(a.id, 'dose', undefined, 10) },
      ]
    : [
        { label: '15 s', go: () => run(a.id, 'pulse', 15) },
        { label: '1 min', go: () => run(a.id, 'pulse', 60) },
        { label: '5 min', go: () => run(a.id, 'pulse', 300) },
        { label: 'Stay on', go: () => run(a.id, 'on') },
      ]
  return (
    <div className="sg-actuator">
      <ActuatorStatus a={a} />
      {canWrite && a.enabled && (
        <div className="sg-actuator-actions" role="group" aria-label={`Control ${a.name}`}>
          {actions.map((action) => (
            <button key={action.label} type="button" className="sg-actuator-btn" onClick={action.go}>{action.label}</button>
          ))}
          <button type="button" className="sg-actuator-btn is-stop" onClick={() => run(a.id, 'off')}>
            <IconPower size={13} /> Off
          </button>
        </div>
      )}
    </div>
  )
}
