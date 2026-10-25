import { Alert, Anchor, Badge, Button, Card, Group, Skeleton, Stack, Text } from '@mantine/core'
import { IconAlertTriangle } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { AiActivity, BedOverview, GardenOverview } from '../api/types'
import { ActuatorControl } from '../components/ActuatorControl'
import { useOverview } from '../components/Layout'
import { AiBadge, CameraImage, ReadingPill } from '../components/Widgets'
import { ago, mediumLabels, titleCase } from '../format'
import { ReservoirCard } from '../components/Reservoir'
import { useAuth } from '../auth'

function gardenHour(localTime: string) {
  const match = localTime.match(/\d{4}-\d{2}-\d{2} (\d{2}):/)
  return match ? Number(match[1]) : new Date().getHours()
}

function BedCard({ b, o }: { b: BedOverview; o: GardenOverview }) {
  const actuators = o.actuators.filter((a) => a.bedId === b.bed.id || b.plants.some((p) => p.plant.id === a.plantId) || (!!b.reservoirId && a.reservoirId === b.reservoirId))
  const camera = o.cameras.find((c) => c.bedId === b.bed.id || b.plants.some((p) => p.plant.id === c.plantId))
  const hydro = b.medium === 'hydroponic'
  const reservoir = b.reservoirId ? o.reservoirs.find((r) => r.id === b.reservoirId) : undefined
  return (
    <Card className={b.aiActive ? 'sg-bed sg-ai-active' : 'sg-bed'} padding={0}>
      <div className="sg-camera-well">
        {camera ? (
          <CameraImage cameraId={camera.id} width={720} alt={camera.name} />
        ) : (
          <div className="sg-camera" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Text size="sm" c="gray.4">No camera on this bed</Text>
          </div>
        )}
      </div>
      <div className="sg-bed-body">
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Anchor component={Link} to={`/beds/${b.bed.id}`} className="sg-serif" fz={22} fw={560} c="inherit" style={{ letterSpacing: '-0.02em' }}>{b.bed.name}</Anchor>
          <AiBadge active={b.aiActive} />
        </Group>
        <Text size="sm" c="dimmed" mt={2}>
          {mediumLabels[b.medium] ?? titleCase(b.medium)}
          {hydro && b.hydroSystem ? ` · ${b.hydroSystem.replaceAll('_', ' ')}` : ''}
          {' · '}{titleCase(b.bed.kind)}
        </Text>
        {reservoir && <div style={{ marginTop: 10 }}><ReservoirCard r={reservoir} compact /></div>}
        {hydro && !reservoir && <Text size="xs" c="red" mt={6}>Hydroponic bed without a reservoir. Link one on the Garden page.</Text>}
        {b.readings.length > 0 && (
          <Group gap={6} mt="sm">
            {b.readings.map((r) => <ReadingPill key={r.channelId} r={r} />)}
          </Group>
        )}
        <Stack gap={8} mt="sm">
          {b.plants.map((p) => (
            <div key={p.plant.id} className={p.aiActive ? 'sg-plant-row sg-ai-active' : 'sg-plant-row'}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Anchor component={Link} to={`/plants/${p.plant.id}`} size="sm" fw={650} c="inherit">
                  {p.plant.name}{p.plant.quantity > 1 ? ` ×${p.plant.quantity}` : ''}
                </Anchor>
                <Group gap={6} wrap="nowrap">
                  <AiBadge active={p.aiActive} label="Acting" />
                  <Badge size="xs" variant="outline" color="gray">{p.plant.stage}</Badge>
                </Group>
              </Group>
              {!hydro && (p.readings.length > 0 || b.readings.length > 0) && (
                <Group gap={6} mt={6}>
                  {(p.readings.length ? p.readings : b.readings.filter((r) => p.targets.some((t) => t.metric === r.metric))).map((r) => (
                    <ReadingPill key={r.channelId} r={r} target={p.targets.find((t) => t.metric === r.metric)} />
                  ))}
                </Group>
              )}
            </div>
          ))}
          {b.plants.length === 0 && <Text size="sm" c="dimmed">No plants yet.</Text>}
        </Stack>
        {actuators.length > 0 && (
          <Stack gap={6} mt="sm" pt="sm" style={{ borderTop: '1px solid var(--sg-line)' }}>
            {actuators.map((a) => <ActuatorControl key={a.id} a={a} />)}
          </Stack>
        )}
      </div>
    </Card>
  )
}

export function DashboardPage() {
  const { data: o, isLoading, error } = useOverview()
  const { user } = useAuth()
  const { data: recent } = useQuery({ queryKey: ['ai-activities'], queryFn: () => api.op<AiActivity[]>('list_ai_activities', { limit: 8 }), refetchInterval: 20_000 })

  if (error) return <Alert color="red" title="Could not load the garden">{(error as Error).message}</Alert>
  if (isLoading || !o) return <Skeleton h={400} />

  const env = o.environment
  const hour = gardenHour(env.localTime)
  const greeting = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening'
  const gardenActuators = o.actuators.filter((a) => !a.bedId && !a.plantId && !a.reservoirId)
  const running = o.runningAiActivities
  const first = user?.displayName.split(' ')[0]

  return (
    <Stack gap="lg">
      {o.activeEmergencyStops.length > 0 && (
        <Alert color="red" variant="filled" icon={<IconAlertTriangle />} title={`${o.activeEmergencyStops.length} emergency stop${o.activeEmergencyStops.length === 1 ? '' : 's'} active`}>
          {o.activeEmergencyStops.map((s) => `${s.scopeType}: ${s.reason}`).join(' · ')}{' '}
          <Anchor component={Link} to="/safety" c="white" fw={700}>Release on the Safety page</Anchor>
        </Alert>
      )}

      <header>
        <p className="sg-kicker">{titleCase(env.season)} · {env.localTime.match(/(\d{2}:\d{2})/)?.[1] ?? env.localTime}{env.locationLabel ? ` · ${env.locationLabel}` : ''}</p>
        <h1 className="sg-garden-title">{o.garden.name}</h1>
        <p className="sg-lede">
          {greeting}{first ? `, ${first}` : ''}. {running.length ? `The assistant is acting on ${running.length === 1 ? 'one thing' : `${running.length} things`}.` : 'The assistant is idle.'}
        </p>
        <div className="sg-status-line">
          <Anchor component={Link} to="/devices">{o.devices.online} of {o.devices.total} devices online{o.devices.pending ? ` · ${o.devices.pending} waiting` : ''}</Anchor>
          <Anchor component={Link} to="/approvals">{o.pendingProposals} waiting for approval</Anchor>
          <span>{env.daylightHours ? `${env.daylightHours.toFixed(1)} h of daylight` : 'Daylight unknown'}{env.sunrise ? ` · ${env.sunrise}–${env.sunset}` : ''}{env.isDaylight === true ? ' · day' : env.isDaylight === false ? ' · night' : ''}</span>
          <Button component={Link} to="/garden" variant="subtle" size="compact-sm">Garden settings</Button>
        </div>
      </header>

      {running.length > 0 && (
        <div className="sg-acting-banner">
          <span className="sg-live-dot" />
          <span>{running.map((a) => a.summary).join(' · ')}</span>
        </div>
      )}

      <div className="sg-beds">
        {o.beds.map((b) => <BedCard key={b.bed.id} b={b} o={o} />)}
      </div>

      {(o.gardenReadings.length > 0 || gardenActuators.length > 0) && (
        <Card>
          <Text fw={650} mb="xs">Garden conditions</Text>
          <Group gap="xs">{o.gardenReadings.map((r) => <ReadingPill key={r.channelId} r={r} />)}</Group>
          {gardenActuators.length > 0 && (
            <Stack gap="xs" mt="md">
              {gardenActuators.map((a) => <ActuatorControl key={a.id} a={a} />)}
            </Stack>
          )}
        </Card>
      )}

      <Card>
        <Group justify="space-between" mb="sm">
          <Text className="sg-serif" fz={22} fw={560}>What the assistant has done</Text>
          <Anchor component={Link} to="/assistant" size="sm" c="inherit">Open the assistant</Anchor>
        </Group>
        {(recent ?? []).length === 0 ? (
          <Text size="sm" c="dimmed">Nothing yet. Ask from the panel beside the garden, or from a plant.</Text>
        ) : (
          <Stack gap={8}>
            {(recent ?? []).map((a) => (
              <div key={a.id}>
                <Text size="sm" fw={600}>{a.summary}</Text>
                <Text size="xs" c="dimmed">{a.status} · {a.kind.replaceAll('_', ' ')} · {ago(a.startedAt)}</Text>
              </div>
            ))}
          </Stack>
        )}
      </Card>
    </Stack>
  )
}
