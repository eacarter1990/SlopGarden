import { Alert, Anchor, Badge, Button, Card, Group, SimpleGrid, Skeleton, Stack, Text, ThemeIcon, Timeline } from '@mantine/core'
import { IconAlertTriangle, IconChecklist, IconCpu, IconSparkles, IconSun, IconPlant2 } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { AiActivity, BedOverview, GardenOverview } from '../api/types'
import { ActuatorControl } from '../components/ActuatorControl'
import { useOverview } from '../components/Layout'
import { AiBadge, CameraImage, PageTitle, ReadingPill } from '../components/Widgets'
import { ago, titleCase } from '../format'
import { useAuth } from '../auth'

function Stat({ icon: Icon, color, label, value, hint, to }: { icon: typeof IconCpu; color: string; label: string; value: React.ReactNode; hint?: React.ReactNode; to?: string }) {
  const body = (
    <Card padding="md" style={{ height: '100%' }}>
      <Group wrap="nowrap" align="flex-start">
        <ThemeIcon size={40} radius="md" variant="light" color={color}><Icon size={22} /></ThemeIcon>
        <div style={{ minWidth: 0 }}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
          <Text fw={750} fz={22} lh={1.2}>{value}</Text>
          {hint && <Text size="xs" c="dimmed" truncate>{hint}</Text>}
        </div>
      </Group>
    </Card>
  )
  return to ? <Anchor component={Link} to={to} underline="never" c="inherit">{body}</Anchor> : body
}

function BedCard({ b, o }: { b: BedOverview; o: GardenOverview }) {
  const actuators = o.actuators.filter((a) => a.bedId === b.bed.id || b.plants.some((p) => p.plant.id === a.plantId))
  const camera = o.cameras.find((c) => c.bedId === b.bed.id || b.plants.some((p) => p.plant.id === c.plantId))
  return (
    <Card className={b.aiActive ? 'sg-ai-active' : undefined} padding="md">
      <Card.Section>
        {camera ? (
          <div style={{ position: 'relative' }}>
            <CameraImage cameraId={camera.id} width={520} alt={camera.name} style={{ borderRadius: 0, aspectRatio: '16 / 7' }} />
            <Badge size="xs" variant="filled" color="dark" style={{ position: 'absolute', left: 8, bottom: 8, opacity: 0.85 }}>● {camera.name}</Badge>
          </div>
        ) : null}
      </Card.Section>
      <Group justify="space-between" mt={camera ? 'sm' : 0} wrap="nowrap">
        <Anchor component={Link} to={`/beds/${b.bed.id}`} fw={700} size="lg" c="inherit">{b.bed.name}</Anchor>
        <Group gap={4} wrap="nowrap">
          <AiBadge active={b.aiActive} />
          <Badge variant="light" color="gray">{titleCase(b.bed.kind)}</Badge>
        </Group>
      </Group>
      {b.readings.length > 0 && (
        <Group gap={6} mt="xs">
          {b.readings.map((r) => <ReadingPill key={r.channelId} r={r} />)}
        </Group>
      )}
      <Stack gap="xs" mt="sm">
        {b.plants.map((p) => (
          <Card key={p.plant.id} padding="xs" radius="md" className={p.aiActive ? 'sg-ai-active' : undefined} bg="var(--mantine-color-default-hover)">
            <Group justify="space-between" wrap="nowrap">
              <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
                <IconPlant2 size={16} color="var(--mantine-color-green-7)" />
                <Anchor component={Link} to={`/plants/${p.plant.id}`} size="sm" fw={600} c="inherit" truncate>
                  {p.plant.name}{p.plant.quantity > 1 ? ` ×${p.plant.quantity}` : ''}
                </Anchor>
              </Group>
              <Group gap={4} wrap="nowrap">
                <AiBadge active={p.aiActive} label="AI" />
                <Badge size="xs" variant="outline" color="green">{p.plant.stage}</Badge>
              </Group>
            </Group>
            {(p.readings.length > 0 || b.readings.length > 0) && (
              <Group gap={6} mt={6}>
                {(p.readings.length ? p.readings : b.readings.filter((r) => p.targets.some((t) => t.metric === r.metric))).map((r) => (
                  <ReadingPill key={r.channelId} r={r} target={p.targets.find((t) => t.metric === r.metric)} />
                ))}
              </Group>
            )}
          </Card>
        ))}
        {b.plants.length === 0 && <Text size="sm" c="dimmed">No plants yet.</Text>}
      </Stack>
      {actuators.length > 0 && (
        <Stack gap={6} mt="sm" pt="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          {actuators.map((a) => <ActuatorControl key={a.id} a={a} />)}
        </Stack>
      )}
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
  const hour = new Date().getHours()
  const greeting = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening'
  const gardenActuators = o.actuators.filter((a) => !a.bedId && !a.plantId)
  const running = o.runningAiActivities

  return (
    <Stack gap="lg">
      <PageTitle
        title={`${greeting}, ${user?.displayName.split(' ')[0]}`}
        subtitle={`${o.garden.name} · ${titleCase(env.season)} · ${env.localTime}${env.locationLabel ? ` · ${env.locationLabel}` : ''}`}
        right={<Button component={Link} to="/garden" variant="light">Edit garden</Button>}
      />

      {o.activeEmergencyStops.length > 0 && (
        <Alert color="red" variant="filled" icon={<IconAlertTriangle />} title={`${o.activeEmergencyStops.length} emergency stop(s) active`}>
          {o.activeEmergencyStops.map((s) => `${s.scopeType}: ${s.reason}`).join(' · ')} — <Anchor component={Link} to="/safety" c="white" fw={700}>Release on the Safety page</Anchor>
        </Alert>
      )}

      <SimpleGrid cols={{ base: 1, xs: 2, lg: 4 }}>
        <Stat icon={IconCpu} color="teal" label="Devices online" value={`${o.devices.online}/${o.devices.total}`} hint={o.devices.pending ? `${o.devices.pending} waiting for approval` : 'all approved'} to="/devices" />
        <Stat icon={IconSparkles} color="violet" label="AI status" value={running.length ? `Acting (${running.length})` : 'Idle'} hint={running[0]?.summary ?? 'No AI action running'} to="/assistant" />
        <Stat icon={IconChecklist} color="grape" label="Pending approvals" value={o.pendingProposals} hint="AI changes waiting for you" to="/approvals" />
        <Stat icon={IconSun} color="yellow" label="Daylight" value={env.daylightHours ? `${env.daylightHours.toFixed(1)} h` : '–'} hint={env.sunrise ? `☀ ${env.sunrise} – ${env.sunset}${env.isDaylight ? ' · day' : ' · night'}` : 'set a location'} />
      </SimpleGrid>

      {o.gardenReadings.length > 0 && (
        <Card>
          <Text fw={650} mb="xs">Garden conditions</Text>
          <Group gap="xs">{o.gardenReadings.map((r) => <ReadingPill key={r.channelId} r={r} />)}</Group>
          {gardenActuators.length > 0 && (
            <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} mt="md" spacing="xs">
              {gardenActuators.map((a) => <Card key={a.id} padding="xs" radius="md"><ActuatorControl a={a} /></Card>)}
            </SimpleGrid>
          )}
        </Card>
      )}

      <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }} spacing="lg">
        {o.beds.map((b) => <BedCard key={b.bed.id} b={b} o={o} />)}
      </SimpleGrid>

      <Card>
        <Group justify="space-between" mb="sm">
          <Text fw={650}>AI activity</Text>
          <Anchor component={Link} to="/assistant" size="sm">Open assistant</Anchor>
        </Group>
        {(recent ?? []).length === 0 ? (
          <Text size="sm" c="dimmed">The AI hasn't done anything yet. Ask it about your garden from the assistant panel.</Text>
        ) : (
          <Timeline bulletSize={18} lineWidth={2}>
            {(recent ?? []).map((a) => (
              <Timeline.Item key={a.id} color={a.status === 'running' ? 'violet' : a.status === 'failed' ? 'red' : 'green'} title={<Text size="sm" fw={600}>{a.summary}</Text>}>
                <Text size="xs" c="dimmed">{a.status} · {a.kind} · {ago(a.startedAt)}</Text>
              </Timeline.Item>
            ))}
          </Timeline>
        )}
      </Card>
    </Stack>
  )
}
