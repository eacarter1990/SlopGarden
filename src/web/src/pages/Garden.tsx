import { ActionIcon, Anchor, Badge, Button, Card, Group, NumberInput, SegmentedControl, Select, SimpleGrid, Stack, Text, TextInput, Textarea } from '@mantine/core'
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
  const { data: timeZones } = useQuery({ queryKey: ['timezones'], queryFn: () => api.get<{ id: string; label: string }[]>('/api/timezones'), staleTime: Infinity })
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
      <Text className="sg-serif" fz={22} fw={560}>Garden settings</Text>
      <div className="sg-form">
        <p className="sg-form-legend">Place</p>
        <TextInput label="Name" value={g.name} onChange={(e) => set('name')(e.currentTarget.value)} disabled={!canWrite} />
        <Select label="Environment" data={opts(environments)} value={g.environment} onChange={set('environment')} disabled={!canWrite} />
        <Select
          label="Growing medium"
          description="Beds use this unless they set their own"
          data={[{ value: 'soil', label: 'Soil' }, { value: 'soilless', label: 'Soilless (coco, perlite, rockwool)' }, { value: 'hydroponic', label: 'Hydroponic' }]}
          value={g.defaultGrowingMedium}
          onChange={set('defaultGrowingMedium')}
          disabled={!canWrite}
        />
        <div className="sg-choice">
          <span className="sg-choice-label">Clock and place</span>
          <SegmentedControl disabled={!canWrite} value={g.locationSource} onChange={(v) => { set('locationSource')(v); set('timezoneSource')(v) }} data={[{ value: 'device', label: 'From the device' }, { value: 'manual', label: 'Set here' }]} />
        </div>
        <NumberInput label="Latitude" decimalScale={4} value={g.latitude ?? ''} onChange={(v) => set('latitude')(v === '' ? null : Number(v))} disabled={!canWrite || g.locationSource !== 'manual'} />
        <NumberInput label="Longitude" decimalScale={4} value={g.longitude ?? ''} onChange={(v) => set('longitude')(v === '' ? null : Number(v))} disabled={!canWrite || g.locationSource !== 'manual'} />
        <Select
          label="Time zone"
          searchable
          nothingFoundMessage="No matching time zone"
          data={(timeZones ?? [{ id: g.timezoneId, label: g.timezoneId }]).map((z) => ({ value: z.id, label: z.label }))}
          value={g.timezoneId}
          onChange={(v) => v && set('timezoneId')(v)}
          disabled={!canWrite || g.timezoneSource !== 'manual'}
        />
        <TextInput label="Hardiness zone" value={g.hardinessZone ?? ''} onChange={(e) => set('hardinessZone')(e.currentTarget.value)} disabled={!canWrite} />
        <p className="sg-form-legend">For the assistant</p>
        <Textarea label="Notes" description="Context the assistant must respect for the whole garden" autosize minRows={2} value={g.aiContext} onChange={(e) => set('aiContext')(e.currentTarget.value)} disabled={!canWrite} />
        <Select
          label="Autonomy"
          disabled={!canWrite}
          value={g.aiAutonomy}
          onChange={set('aiAutonomy')}
          data={[
            { value: 'standing_approvals', label: 'Ask first; routine actions marked “always allow” may repeat' },
            { value: 'propose_all', label: 'Always ask before every change' },
          ]}
        />
        {canWrite && <div className="sg-form-actions"><Button onClick={submit}>Save garden</Button></div>}
      </div>
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
      <SimpleGrid cols={{ base: 1, lg: 2 }}>
        {(beds ?? []).map((b) => (
          <Card key={b.id}>
            <Group justify="space-between">
              <Anchor component={Link} to={`/beds/${b.id}`} fw={700} c="inherit">{b.name}</Anchor>
              <Group gap={4}>
                <Badge variant="outline" color="gray">
                  {mediumLabels[b.growingMedium ?? o.garden.defaultGrowingMedium]}{b.growingMedium ? '' : ' (default)'}
                </Badge>
                {b.hydroSystem && (b.growingMedium ?? o.garden.defaultGrowingMedium) === 'hydroponic' && <Badge variant="outline" color="gray">{systems?.find((s) => s.id === b.hydroSystem)?.label ?? b.hydroSystem}</Badge>}
                <Badge variant="outline" color="gray">{titleCase(b.kind)}</Badge>
                <Badge variant="outline" color="gray">{titleCase(b.sunExposure)}</Badge>
                {canWrite && <ActionIcon variant="subtle" onClick={() => openBedForm(o.garden.id, b, o.garden.defaultGrowingMedium)}><IconPencil size={16} /></ActionIcon>}
              </Group>
            </Group>
            {b.aiContext && <Text size="xs" c="dimmed" mt={4}>AI note: {b.aiContext}</Text>}
            <div className="sg-stack" style={{ marginTop: 10 }}>
              {(plants ?? []).filter((p) => p.bedId === b.id).map((p) => (
                <div key={p.id} className="sg-line">
                  <Anchor component={Link} to={`/plants/${p.id}`} fw={600} c="inherit">{p.name}{p.quantity > 1 ? ` ×${p.quantity}` : ''}</Anchor>
                  <Text size="sm" c="dimmed">{p.profileName ?? 'No profile'}</Text>
                  <Badge size="xs" variant="outline">{p.stage}</Badge>
                  {canWrite && <ActionIcon size="sm" variant="subtle" aria-label={`Edit ${p.name}`} onClick={() => openPlantForm(b.id, p)}><IconPencil size={14} /></ActionIcon>}
                </div>
              ))}
            </div>
            {canWrite && <Button mt="xs" size="xs" variant="light" leftSection={<IconPlus size={14} />} onClick={() => openPlantForm(b.id)}>Add plant</Button>}
          </Card>
        ))}
      </SimpleGrid>
      <GardenSettings garden={o.garden} />
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
        <SimpleGrid cols={{ base: 1, sm: 2, xl: 3 }}>
          {(profiles ?? []).map((p) => (
            <div key={p.id} className="sg-record">
              <Group justify="space-between" align="flex-start">
                <div>
                  <Text fw={650}>{p.commonName}</Text>
                  <Text size="xs" c="dimmed" fs="italic">{p.scientificName}</Text>
                </div>
                <Badge size="xs" variant="light" color="gray">{p.isBuiltin ? 'built-in' : p.createdByType}</Badge>
              </Group>
              <Text size="sm">Water {p.waterNeed} · moisture {p.soilMoistureMinPct}–{p.soilMoistureMaxPct}% · light {p.lightHoursMin}–{p.lightHoursMax} h</Text>
              <Text size="sm" c="dimmed">Air {p.airTempMinC}–{p.airTempMaxC} °C · pH {p.phMin}–{p.phMax}{p.ecMin != null ? ` · EC ${p.ecMin}–${p.ecMax}` : ''} · {p.daysToMaturityMin}–{p.daysToMaturityMax} days</Text>
            </div>
          ))}
        </SimpleGrid>
      </Card>
    </Stack>
  )
}
