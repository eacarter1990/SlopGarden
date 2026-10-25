import { ActionIcon, Badge, Button, Group, NumberInput, Select, Stack, Switch, Text, TextInput, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconPlus, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Actuator, Schedule } from '../api/types'
import { useAuth } from '../auth'
import { useSave } from '../components/Forms'
import { PageTitle } from '../components/Widgets'
import { when } from '../format'

interface ScheduleRow {
  schedule: Schedule
  actuatorName?: string | null
  nextRuns: string[]
}

const presets = [
  { value: '0 7 * * *', label: 'Daily at 07:00' },
  { value: '0 7,19 * * *', label: 'Twice a day (07:00, 19:00)' },
  { value: '0 6 * * 1,3,5', label: 'Mon/Wed/Fri at 06:00' },
  { value: '*/30 * * * *', label: 'Every 30 minutes' },
  { value: '0 */4 * * *', label: 'Every 4 hours' },
]

function ScheduleForm() {
  const save = useSave()
  const { data: actuators } = useQuery({ queryKey: ['actuators'], queryFn: () => api.op<Actuator[]>('list_actuators') })
  const [kind, setKind] = useState<string | null>('actuator')
  const [name, setName] = useState('')
  const [cron, setCron] = useState('0 7 * * *')
  const [actuatorId, setActuatorId] = useState<string | null>(null)
  const [duration, setDuration] = useState<number | string>(60)
  const [prompt, setPrompt] = useState('Check soil moisture against each plant’s target and water if needed.')
  return (
    <Stack>
      <Select label="Kind" value={kind} onChange={setKind} allowDeselect={false} data={[
        { value: 'actuator', label: 'Run an actuator (runs on the device, works offline)' },
        { value: 'ai_review', label: 'AI review — how often garden data is sent to the model' },
        { value: 'camera_snapshot', label: 'Save camera snapshots (growth history)' },
        { value: 'report', label: 'AI garden report (sent as a notification)' },
      ]} />
      <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} required />
      <Group grow>
        <Select label="When" data={presets} value={presets.some((p) => p.value === cron) ? cron : null} onChange={(v) => v && setCron(v)} placeholder="Custom" />
        <TextInput label="Cron (garden timezone)" value={cron} onChange={(e) => setCron(e.currentTarget.value)} ff="monospace" />
      </Group>
      {kind === 'actuator' && (
        <Group grow>
          <Select label="Actuator" data={(actuators ?? []).map((a) => ({ value: a.id, label: `${a.name} (${a.kind})` }))} value={actuatorId} onChange={setActuatorId} searchable />
          <NumberInput label="Run for (seconds)" min={1} value={duration} onChange={setDuration} />
        </Group>
      )}
      {(kind === 'ai_review' || kind === 'report') && <Textarea label="Instruction for the AI" autosize minRows={2} value={prompt} onChange={(e) => setPrompt(e.currentTarget.value)} />}
      <Group justify="flex-end">
        <Button disabled={!name} onClick={async () => {
          await save('create_schedule', { name, kind, cron, actuatorId, durationSec: kind === 'actuator' ? Number(duration) : undefined, payload: kind === 'ai_review' || kind === 'report' ? { prompt } : undefined }, 'Schedule created')
          modals.closeAll()
        }}>Create</Button>
      </Group>
    </Stack>
  )
}

export function AutomationPage() {
  const { canWrite } = useAuth()
  const save = useSave()
  const { data } = useQuery({ queryKey: ['schedules'], queryFn: () => api.op<ScheduleRow[]>('list_schedules') })
  return (
    <Stack>
      <PageTitle
        title="Automation"
        subtitle="Schedules. Actuator schedules are pushed to the device and keep running while it's offline, always within safety limits."
        right={canWrite && <Button leftSection={<IconPlus size={16} />} onClick={() => modals.open({ title: 'New schedule', size: 'lg', children: <ScheduleForm /> })}>New schedule</Button>}
      />
      <div className="sg-stack">
        {(data ?? []).map(({ schedule: s, actuatorName, nextRuns }) => (
          <div key={s.id} className="sg-record sg-record-wide">
            <div>
              <Group gap={8}>
                <Text fw={650}>{s.name}</Text>
                <Badge variant="light" color="gray">{s.kind === 'ai_review' ? 'AI review' : s.kind.replaceAll('_', ' ')}</Badge>
                <Badge size="xs" variant="outline" color="gray">{s.createdByType}</Badge>
              </Group>
              <Text size="sm" mt={4}>{s.kind === 'actuator' ? `${actuatorName} for ${s.durationSec}s` : String(s.payload?.prompt ?? '')}</Text>
              <Text size="xs" c="dimmed" mt={2}>{s.cron} · {s.timezoneId}</Text>
            </div>
            <Text size="sm">{s.enabled ? `Next ${when(nextRuns[0])}` : 'Paused'}</Text>
            <Group gap="xs" wrap="nowrap">
              <Switch size="sm" checked={s.enabled} disabled={!canWrite} aria-label={`Enable ${s.name}`} onChange={(e) => save('update_schedule', { scheduleId: s.id, enabled: e.currentTarget.checked })} />
              {canWrite && <ActionIcon variant="subtle" color="red" aria-label={`Delete ${s.name}`} onClick={() => save('delete_schedule', { scheduleId: s.id }, 'Deleted')}><IconTrash size={16} /></ActionIcon>}
            </Group>
          </div>
        ))}
        {data?.length === 0 && <div className="sg-empty">No schedules yet. Create one, or ask the assistant (for example, “water the tomatoes every morning”).</div>}
      </div>
    </Stack>
  )
}
