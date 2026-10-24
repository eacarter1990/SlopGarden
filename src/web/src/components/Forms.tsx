import { Button, Group, NumberInput, Select, SimpleGrid, Stack, TextInput, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Bed, Plant, PlantProfile } from '../api/types'
import { titleCase } from '../format'

export const opts = (xs: string[]) => xs.map((x) => ({ value: x, label: titleCase(x) }))
export const bedKinds = ['raised_bed', 'in_ground', 'container', 'greenhouse_bench', 'hydroponic', 'vertical', 'orchard_row', 'other']
export const irrigation = ['drip', 'sprinkler', 'soaker', 'manual', 'hydroponic', 'none']
export const sun = ['full_sun', 'part_sun', 'part_shade', 'full_shade', 'grow_light']
export const stages = ['planned', 'seed', 'germinating', 'seedling', 'vegetative', 'flowering', 'fruiting', 'harvesting', 'dormant']
export const environments = ['outdoor', 'indoor', 'greenhouse', 'mixed']

export function useSave() {
  const qc = useQueryClient()
  return async (tool: string, args: Record<string, unknown>, success = 'Saved') => {
    try {
      const r = await api.op(tool, args)
      notifications.show({ color: 'green', message: success })
      qc.invalidateQueries()
      return r
    } catch (e) {
      notifications.show({ color: 'red', title: 'Failed', message: (e as Error).message })
      throw e
    }
  }
}

function clean(o: Record<string, unknown>) {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== '' && v !== undefined))
}

function BedForm({ bed, gardenId }: { bed?: Bed; gardenId: string }) {
  const save = useSave()
  const [v, setV] = useState<Record<string, unknown>>(bed ? { ...bed } : { kind: 'raised_bed', irrigationType: 'drip', sunExposure: 'full_sun' })
  const set = (k: string) => (x: unknown) => setV((s) => ({ ...s, [k]: x }))
  const submit = async () => {
    const args = clean({ name: v.name, kind: v.kind, irrigationType: v.irrigationType, sunExposure: v.sunExposure, widthCm: v.widthCm, lengthCm: v.lengthCm, depthCm: v.depthCm, soilType: v.soilType, aiContext: v.aiContext, notes: v.notes })
    await save(bed ? 'update_bed' : 'create_bed', bed ? { bedId: bed.id, ...args } : { gardenId, ...args })
    modals.closeAll()
  }
  return (
    <Stack>
      <TextInput label="Name" value={(v.name as string) ?? ''} onChange={(e) => set('name')(e.currentTarget.value)} required data-autofocus />
      <SimpleGrid cols={3}>
        <Select label="Kind" data={opts(bedKinds)} value={v.kind as string} onChange={set('kind')} />
        <Select label="Irrigation" data={opts(irrigation)} value={v.irrigationType as string} onChange={set('irrigationType')} />
        <Select label="Sun" data={opts(sun)} value={v.sunExposure as string} onChange={set('sunExposure')} />
      </SimpleGrid>
      <SimpleGrid cols={3}>
        <NumberInput label="Width (cm)" value={(v.widthCm as number) ?? ''} onChange={set('widthCm')} />
        <NumberInput label="Length (cm)" value={(v.lengthCm as number) ?? ''} onChange={set('lengthCm')} />
        <NumberInput label="Depth (cm)" value={(v.depthCm as number) ?? ''} onChange={set('depthCm')} />
      </SimpleGrid>
      <TextInput label="Soil" value={(v.soilType as string) ?? ''} onChange={(e) => set('soilType')(e.currentTarget.value)} />
      <Textarea label="Notes for the AI" description="Anything the AI should know about this bed" autosize minRows={2} value={(v.aiContext as string) ?? ''} onChange={(e) => set('aiContext')(e.currentTarget.value)} />
      <Group justify="flex-end"><Button onClick={submit}>{bed ? 'Save bed' : 'Create bed'}</Button></Group>
    </Stack>
  )
}

export const openBedForm = (gardenId: string, bed?: Bed) => modals.open({ title: bed ? `Edit ${bed.name}` : 'New bed', size: 'lg', children: <BedForm gardenId={gardenId} bed={bed} /> })

function PlantForm({ plant, bedId }: { plant?: Plant; bedId: string }) {
  const save = useSave()
  const { data: profiles } = useQuery({ queryKey: ['profiles'], queryFn: () => api.op<PlantProfile[]>('list_plant_profiles') })
  const [v, setV] = useState<Record<string, unknown>>(plant ? { ...plant, profile: plant.plantProfileId } : { stage: 'seedling', quantity: 1, startedFrom: 'seed' })
  const set = (k: string) => (x: unknown) => setV((s) => ({ ...s, [k]: x }))
  const submit = async () => {
    const args = clean({ name: v.name, profile: v.profile, variety: v.variety, quantity: v.quantity, stage: v.stage, startedFrom: v.startedFrom, plantedAt: v.plantedAt, expectedHarvestStart: v.expectedHarvestStart, aiContext: v.aiContext, notes: v.notes })
    await save(plant ? 'update_plant' : 'create_plant', plant ? { plantId: plant.id, ...args } : { bedId, ...args })
    modals.closeAll()
  }
  return (
    <Stack>
      <SimpleGrid cols={2}>
        <TextInput label="Name" value={(v.name as string) ?? ''} onChange={(e) => set('name')(e.currentTarget.value)} required data-autofocus />
        <Select label="Crop profile" searchable data={(profiles ?? []).map((p) => ({ value: p.id, label: p.commonName }))} value={(v.profile as string) ?? null} onChange={set('profile')} />
      </SimpleGrid>
      <SimpleGrid cols={3}>
        <TextInput label="Variety" value={(v.variety as string) ?? ''} onChange={(e) => set('variety')(e.currentTarget.value)} />
        <NumberInput label="Quantity" min={1} value={(v.quantity as number) ?? 1} onChange={set('quantity')} />
        <Select label="Stage" data={opts(stages)} value={v.stage as string} onChange={set('stage')} />
      </SimpleGrid>
      <SimpleGrid cols={3}>
        <Select label="Started from" data={opts(['seed', 'transplant', 'cutting', 'unknown'])} value={v.startedFrom as string} onChange={set('startedFrom')} />
        <TextInput type="date" label="Planted" value={(v.plantedAt as string) ?? ''} onChange={(e) => set('plantedAt')(e.currentTarget.value)} />
        <TextInput type="date" label="Harvest from" value={(v.expectedHarvestStart as string) ?? ''} onChange={(e) => set('expectedHarvestStart')(e.currentTarget.value)} />
      </SimpleGrid>
      <Textarea label="Notes for the AI" autosize minRows={2} value={(v.aiContext as string) ?? ''} onChange={(e) => set('aiContext')(e.currentTarget.value)} />
      <Group justify="flex-end"><Button onClick={submit}>{plant ? 'Save plant' : 'Add plant'}</Button></Group>
    </Stack>
  )
}

export const openPlantForm = (bedId: string, plant?: Plant) => modals.open({ title: plant ? `Edit ${plant.name}` : 'Add plant', size: 'lg', children: <PlantForm bedId={bedId} plant={plant} /> })
