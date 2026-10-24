import { ActionIcon, Anchor, Badge, Button, Card, Group, NumberInput, SegmentedControl, Select, SimpleGrid, Stack, Table, Text, TextInput, Textarea } from '@mantine/core'
import { IconPencil, IconPlus } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { Bed, Garden, Plant, PlantProfile } from '../api/types'
import { useAuth } from '../auth'
import { environments, openBedForm, openPlantForm, opts, useSave } from '../components/Forms'
import { useOverview } from '../components/Layout'
import { PageTitle } from '../components/Widgets'
import { ReservoirActions, ReservoirCard, openReservoirForm, useHydroSystems, useReservoirs } from '../components/Reservoir'
import { mediumLabels, titleCase } from '../format'
import { usePageEntity } from '../pageContext'

function GardenSettings({ garden }: { garden: Garden }) {
  const { canWrite } = useAuth()
  const save = useSave()
  const [g, setG] = useState(garden)
  useEffect(() => setG(garden), [garden])
  const set = (k: keyof Garden) => (v: unknown) => setG((s) => ({ ...s, [k]: v }))
  const submit = () =>
    save('update_garden', {
      gardenId: g.id, name: g.name, description: g.description, environment: g.environment, locationSource: g.locationSource, timezoneSource: g.timezoneSource,
      ...(g.locationSource === 'manual' ? { latitude: g.latitude, longitude: g.longitude, locationLabel: g.locationLabel } : {}),
      ...(g.timezoneSource === 'manual' ? { timezoneId: g.timezoneId } : {}),
      hardinessZone: g.hardinessZone ?? undefined, aiContext: g.aiContext, aiAutonomy: g.aiAutonomy, defaultGrowingMedium: g.defaultGrowingMedium,
    }, 'Garden saved')
  return (
    <Card>
      <Text fw={650} mb="sm">Garden settings</Text>
      <Stack gap="sm">
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Name" value={g.name} onChange={(e) => set('name')(e.currentTarget.value)} disabled={!canWrite} />
          <Select label="Environment" data={opts(environments)} value={g.environment} onChange={set('environment')} disabled={!canWrite} />
          <Select
            label="Default growing medium"
            description="Beds use this unless they set their own"
            data={[{ value: 'soil', label: 'Soil' }, { value: 'soilless', label: 'Soilless (coco, perlite, rockwool)' }, { value: 'hydroponic', label: 'Hydroponic' }]}
            value={g.defaultGrowingMedium}
            onChange={set('defaultGrowingMedium')}
            disabled={!canWrite}
          />
        </SimpleGrid>
        <div>
          <Text size="sm" fw={500} mb={4}>Location & time</Text>
          <SegmentedControl size="xs" disabled={!canWrite} value={g.locationSource} onChange={(v) => { set('locationSource')(v); set('timezoneSource')(v) }} data={[{ value: 'device', label: 'Inherit from device' }, { value: 'manual', label: 'Set manually' }]} />
        </div>
        <SimpleGrid cols={{ base: 1, sm: 4 }}>
          <NumberInput label="Latitude" decimalScale={4} value={g.latitude ?? ''} onChange={(v) => set('latitude')(v === '' ? null : Number(v))} disabled={!canWrite || g.locationSource !== 'manual'} />
          <NumberInput label="Longitude" decimalScale={4} value={g.longitude ?? ''} onChange={(v) => set('longitude')(v === '' ? null : Number(v))} disabled={!canWrite || g.locationSource !== 'manual'} />
          <TextInput label="Timezone" value={g.timezoneId} onChange={(e) => set('timezoneId')(e.currentTarget.value)} disabled={!canWrite || g.timezoneSource !== 'manual'} />
          <TextInput label="Hardiness zone" value={g.hardinessZone ?? ''} onChange={(e) => set('hardinessZone')(e.currentTarget.value)} disabled={!canWrite} />
        </SimpleGrid>
        <Textarea label="Notes for the AI" description="Context the AI must respect for the whole garden" autosize minRows={2} value={g.aiContext} onChange={(e) => set('aiContext')(e.currentTarget.value)} disabled={!canWrite} />
        <Select
          label="AI autonomy"
          disabled={!canWrite}
          value={g.aiAutonomy}
          onChange={set('aiAutonomy')}
          data={[
            { value: 'standing_approvals', label: 'Ask first; routine actions I marked "always allow" may repeat on their own' },
            { value: 'propose_all', label: 'Always ask before every change' },
          ]}
        />
        {canWrite && <Group justify="flex-end"><Button onClick={submit}>Save garden</Button></Group>}
      </Stack>
    </Card>
  )
}

export function GardenPage() {
  const { data: o } = useOverview()
  const { canWrite } = useAuth()
  const { data: beds } = useQuery({ queryKey: ['beds'], queryFn: () => api.op<Bed[]>('list_beds') })
  const { data: plants } = useQuery({ queryKey: ['plants'], queryFn: () => api.op<Plant[]>('list_plants') })
  const { data: profiles } = useQuery({ queryKey: ['profiles'], queryFn: () => api.op<PlantProfile[]>('list_plant_profiles') })
  const { data: reservoirs } = useReservoirs()
  const { data: systems } = useHydroSystems()
  usePageEntity('garden', o?.garden.id, o ? `Garden: ${o.garden.name}` : undefined)
  if (!o) return null
  return (
    <Stack gap="lg">
      <PageTitle title="Garden" subtitle="Beds, plants and the context the AI works with" right={canWrite && <Button leftSection={<IconPlus size={16} />} onClick={() => openBedForm(o.garden.id, undefined, o.garden.defaultGrowingMedium)}>New bed</Button>} />
      <GardenSettings garden={o.garden} />
      <SimpleGrid cols={{ base: 1, lg: 2 }}>
        {(beds ?? []).map((b) => (
          <Card key={b.id}>
            <Group justify="space-between">
              <Anchor component={Link} to={`/beds/${b.id}`} fw={700} c="inherit">{b.name}</Anchor>
              <Group gap={4}>
                <Badge variant="filled" color={(b.growingMedium ?? o.garden.defaultGrowingMedium) === 'hydroponic' ? 'blue' : (b.growingMedium ?? o.garden.defaultGrowingMedium) === 'soilless' ? 'grape' : 'green'}>
                  {mediumLabels[b.growingMedium ?? o.garden.defaultGrowingMedium]}{b.growingMedium ? '' : ' (default)'}
                </Badge>
                {b.hydroSystem && (b.growingMedium ?? o.garden.defaultGrowingMedium) === 'hydroponic' && <Badge variant="light" color="blue">{systems?.find((s) => s.id === b.hydroSystem)?.label ?? b.hydroSystem}</Badge>}
                <Badge variant="light">{titleCase(b.kind)}</Badge>
                <Badge variant="light" color="yellow">{titleCase(b.sunExposure)}</Badge>
                {canWrite && <ActionIcon variant="subtle" onClick={() => openBedForm(o.garden.id, b, o.garden.defaultGrowingMedium)}><IconPencil size={16} /></ActionIcon>}
              </Group>
            </Group>
            {b.aiContext && <Text size="xs" c="dimmed" mt={4}>AI note: {b.aiContext}</Text>}
            <Table mt="sm" fz="sm" verticalSpacing={4}>
              <Table.Tbody>
                {(plants ?? []).filter((p) => p.bedId === b.id).map((p) => (
                  <Table.Tr key={p.id}>
                    <Table.Td><Anchor component={Link} to={`/plants/${p.id}`}>{p.name}</Anchor>{p.quantity > 1 && <Text span c="dimmed"> ×{p.quantity}</Text>}</Table.Td>
                    <Table.Td c="dimmed">{p.profileName ?? '—'}</Table.Td>
                    <Table.Td><Badge size="xs" variant="outline">{p.stage}</Badge></Table.Td>
                    <Table.Td w={30}>{canWrite && <ActionIcon size="sm" variant="subtle" onClick={() => openPlantForm(b.id, p)}><IconPencil size={14} /></ActionIcon>}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
            {canWrite && <Button mt="xs" size="xs" variant="light" leftSection={<IconPlus size={14} />} onClick={() => openPlantForm(b.id)}>Add plant</Button>}
          </Card>
        ))}
      </SimpleGrid>
      <Card>
        <Group justify="space-between" mb="xs">
          <div>
            <Text fw={650}>Reservoirs</Text>
            <Text size="sm" c="dimmed">Nutrient tanks for hydroponic and soilless beds. EC, pH, water temperature, level and flow sensors, pumps and dosers attach to a reservoir.</Text>
          </div>
          {canWrite && <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => openReservoirForm()}>New reservoir</Button>}
        </Group>
        <SimpleGrid cols={{ base: 1, lg: 2 }}>
          {(reservoirs ?? []).map((r) => (
            <Stack key={r.id} gap={6}>
              <ReservoirCard r={r} />
              <Text size="xs" c="dimmed">Feeds: {r.beds.map((b) => b.name).join(', ') || 'no beds yet'}</Text>
              <ReservoirActions r={r} />
            </Stack>
          ))}
        </SimpleGrid>
        {reservoirs?.length === 0 && <Text size="sm" c="dimmed">No reservoirs yet.</Text>}
      </Card>
      <Card>
        <Text fw={650} mb="xs">Crop profiles ({profiles?.length ?? 0})</Text>
        <Text size="sm" c="dimmed" mb="sm">Water, light and temperature needs the AI uses. The AI can research and tweak these (with your approval).</Text>
        <Table striped fz="xs">
          <Table.Thead>
            <Table.Tr><Table.Th>Crop</Table.Th><Table.Th>Water</Table.Th><Table.Th>Soil moisture</Table.Th><Table.Th>Light h</Table.Th><Table.Th>Air °C</Table.Th><Table.Th>pH</Table.Th><Table.Th>Hydro EC</Table.Th><Table.Th>Days</Table.Th><Table.Th>Source</Table.Th></Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(profiles ?? []).map((p) => (
              <Table.Tr key={p.id}>
                <Table.Td><Text size="xs" fw={600}>{p.commonName}</Text><Text size="xs" c="dimmed" fs="italic">{p.scientificName}</Text></Table.Td>
                <Table.Td>{p.waterNeed}</Table.Td>
                <Table.Td>{p.soilMoistureMinPct}–{p.soilMoistureMaxPct} %</Table.Td>
                <Table.Td>{p.lightHoursMin}–{p.lightHoursMax}</Table.Td>
                <Table.Td>{p.airTempMinC}–{p.airTempMaxC}</Table.Td>
                <Table.Td>{p.phMin}–{p.phMax}</Table.Td>
                <Table.Td>{p.ecMin != null ? `${p.ecMin}–${p.ecMax}` : '–'}</Table.Td>
                <Table.Td>{p.daysToMaturityMin}–{p.daysToMaturityMax}</Table.Td>
                <Table.Td><Badge size="xs" variant="light" color={p.createdByType === 'ai' ? 'violet' : 'gray'}>{p.isBuiltin ? 'built-in' : p.createdByType}</Badge></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Card>
    </Stack>
  )
}
