import { Anchor, Badge, Button, Card, Grid, Group, SimpleGrid, Stack, Text } from '@mantine/core'
import { IconPencil, IconPlus } from '@tabler/icons-react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../auth'
import { ActuatorControl } from '../components/ActuatorControl'
import { EmergencyStopButton } from '../components/EmergencyStopButton'
import { openBedForm, openPlantForm } from '../components/Forms'
import { useOverview } from '../components/Layout'
import { AiBadge, CameraImage, PageTitle, ReadingPill } from '../components/Widgets'
import { titleCase } from '../format'
import { usePageEntity } from '../pageContext'

export function BedPage() {
  const { id } = useParams()
  const { canWrite } = useAuth()
  const { data: o } = useOverview()
  const b = o?.beds.find((x) => x.bed.id === id)
  usePageEntity('bed', id, b ? `Bed: ${b.bed.name}` : undefined)
  if (!o || !b) return null
  const plantIds = new Set(b.plants.map((p) => p.plant.id))
  const cams = o.cameras.filter((c) => c.bedId === id || (c.plantId && plantIds.has(c.plantId)))
  const acts = o.actuators.filter((a) => a.bedId === id || (a.plantId && plantIds.has(a.plantId)))
  return (
    <Stack gap="lg">
      <PageTitle
        title={b.bed.name}
        subtitle={`${titleCase(b.bed.kind)} · ${titleCase(b.bed.sunExposure)} · ${titleCase(b.bed.irrigationType)} irrigation${b.bed.widthCm ? ` · ${b.bed.widthCm}×${b.bed.lengthCm} cm` : ''}`}
        right={
          <>
            <AiBadge active={b.aiActive} />
            {canWrite && <Button variant="light" leftSection={<IconPencil size={16} />} onClick={() => openBedForm(o.garden.id, b.bed)}>Edit</Button>}
            {canWrite && <Button variant="light" leftSection={<IconPlus size={16} />} onClick={() => openPlantForm(b.bed.id)}>Add plant</Button>}
            <EmergencyStopButton scopeType="bed" scopeId={b.bed.id} label="Stop bed" />
          </>
        }
      />
      <Grid>
        <Grid.Col span={{ base: 12, lg: 8 }}>
          <Stack>
            <SimpleGrid cols={{ base: 1, sm: cams.length > 1 ? 2 : 1 }}>
              {cams.map((c) => (
                <Card key={c.id} padding={0}>
                  <CameraImage cameraId={c.id} width={cams.length > 1 ? 640 : 1100} alt={c.name} />
                  <Text size="sm" fw={600} p="xs">{c.name}</Text>
                </Card>
              ))}
            </SimpleGrid>
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              {b.plants.map((p) => (
                <Card key={p.plant.id} className={p.aiActive ? 'sg-ai-active' : undefined}>
                  <Group justify="space-between">
                    <Anchor component={Link} to={`/plants/${p.plant.id}`} fw={650}>{p.plant.name}</Anchor>
                    <Badge size="sm" variant="outline">{p.plant.stage}</Badge>
                  </Group>
                  <Text size="xs" c="dimmed">{p.plant.profileName}{p.plant.quantity > 1 ? ` · ${p.plant.quantity} plants` : ''}</Text>
                  <Group gap={6} mt="xs">
                    {(p.readings.length ? p.readings : b.readings).map((r) => <ReadingPill key={r.channelId} r={r} target={p.targets.find((t) => t.metric === r.metric)} />)}
                  </Group>
                </Card>
              ))}
            </SimpleGrid>
          </Stack>
        </Grid.Col>
        <Grid.Col span={{ base: 12, lg: 4 }}>
          <Stack>
            <Card>
              <Text fw={650} mb="xs">Bed readings</Text>
              <Group gap={6}>{b.readings.length ? b.readings.map((r) => <ReadingPill key={r.channelId} r={r} />) : <Text size="sm" c="dimmed">No bed-level sensors.</Text>}</Group>
            </Card>
            <Card>
              <Text fw={650} mb="xs">Actuators</Text>
              <Stack gap={8}>{acts.length ? acts.map((a) => <ActuatorControl key={a.id} a={a} />) : <Text size="sm" c="dimmed">None.</Text>}</Stack>
            </Card>
            {b.bed.aiContext && <Card><Text fw={650}>Notes for the AI</Text><Text size="sm">{b.bed.aiContext}</Text></Card>}
          </Stack>
        </Grid.Col>
      </Grid>
    </Stack>
  )
}
