import { Button, NumberInput, SegmentedControl, Select, TextInput, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { Bed, HydroSystemInfo, Plant, PlantProfile, ReservoirStatus } from '../api/types'
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

function BedForm({ bed, gardenId, gardenDefault }: { bed?: Bed; gardenId: string; gardenDefault: string }) {
  const save = useSave()
  const { data: systems } = useQuery({ queryKey: ['hydro-systems'], queryFn: () => api.op<HydroSystemInfo[]>('list_hydro_systems'), staleTime: Infinity })
  const { data: reservoirs } = useQuery({ queryKey: ['reservoirs'], queryFn: () => api.op<ReservoirStatus[]>('list_reservoirs') })
  const [v, setV] = useState<Record<string, unknown>>(bed ? { ...bed, growingMedium: bed.growingMedium ?? 'default' } : { kind: 'raised_bed', irrigationType: 'drip', sunExposure: 'full_sun', growingMedium: 'default' })
  const set = (k: string) => (x: unknown) => setV((s) => ({ ...s, [k]: x }))
  const medium = v.growingMedium === 'default' ? gardenDefault : (v.growingMedium as string)
  const hydro = medium === 'hydroponic'
  const system = systems?.find((s) => s.id === v.hydroSystem)
  const submit = async () => {
    const args = clean({
      name: v.name, kind: v.kind, irrigationType: hydro ? 'hydroponic' : v.irrigationType, sunExposure: v.sunExposure, widthCm: v.widthCm, lengthCm: v.lengthCm, depthCm: v.depthCm,
      soilType: hydro ? undefined : v.soilType, aiContext: v.aiContext, notes: v.notes,
      growingMedium: v.growingMedium === 'default' ? undefined : v.growingMedium, hydroSystem: hydro ? v.hydroSystem : undefined, reservoirId: medium !== 'soil' ? v.reservoirId : undefined,
    })
    await save(bed ? 'update_bed' : 'create_bed', bed ? { bedId: bed.id, ...args } : { gardenId, ...args })
    modals.closeAll()
  }
  return (
    <div className="sg-form">
      <TextInput label="Name" value={(v.name as string) ?? ''} onChange={(e) => set('name')(e.currentTarget.value)} required data-autofocus />
      <div className="sg-choice">
        <span className="sg-choice-label">Medium</span>
        <SegmentedControl
          value={v.growingMedium as string}
          onChange={set('growingMedium')}
          data={[{ value: 'default', label: `Default (${mediumLabel(gardenDefault)})` }, { value: 'soil', label: 'Soil' }, { value: 'soilless', label: 'Soilless' }, { value: 'hydroponic', label: 'Hydro' }]}
        />
      </div>
      {hydro && (
        <Select
          label="Hydroponic system"
          data={(systems ?? []).map((s) => ({ value: s.id, label: s.label }))}
          value={(v.hydroSystem as string) ?? null}
          onChange={set('hydroSystem')}
          description={system ? `${system.description} ${system.guidance}` : 'Pick the system type; it decides which pumps must run and which alarms apply.'}
        />
      )}
      {medium !== 'soil' && (
        <Select
          label="Fed by reservoir"
          clearable
          data={(reservoirs ?? []).map((r) => ({ value: r.id, label: `${r.name}${r.volumeLiters ? ` (${r.volumeLiters} L)` : ''}` }))}
          value={(v.reservoirId as string) ?? null}
          onChange={set('reservoirId')}
          description="Create reservoirs on the Garden page. EC, pH, water temperature and flow are tracked on the reservoir."
        />
      )}
      <Select label="Kind" data={opts(bedKinds)} value={v.kind as string} onChange={set('kind')} />
      {!hydro && <Select label="Irrigation" data={opts(irrigation)} value={v.irrigationType as string} onChange={set('irrigationType')} />}
      <Select label="Sun" data={opts(sun)} value={v.sunExposure as string} onChange={set('sunExposure')} />
      <NumberInput label="Width" description="Centimetres" value={(v.widthCm as number) ?? ''} onChange={set('widthCm')} />
      <NumberInput label="Length" description="Centimetres" value={(v.lengthCm as number) ?? ''} onChange={set('lengthCm')} />
      <NumberInput label="Depth" description="Centimetres" value={(v.depthCm as number) ?? ''} onChange={set('depthCm')} />
      {!hydro && <TextInput label={medium === 'soilless' ? 'Substrate' : 'Soil'} value={(v.soilType as string) ?? ''} onChange={(e) => set('soilType')(e.currentTarget.value)} />}
      <Textarea label="Notes" description="Anything the assistant should know about this bed" autosize minRows={2} value={(v.aiContext as string) ?? ''} onChange={(e) => set('aiContext')(e.currentTarget.value)} />
      <div className="sg-form-actions"><Button onClick={submit}>{bed ? 'Save bed' : 'Create bed'}</Button></div>
    </div>
  )
}

const mediumLabel = (m: string) => ({ soil: 'Soil', soilless: 'Soilless', hydroponic: 'Hydroponic' })[m] ?? m

export const openBedForm = (gardenId: string, bed?: Bed, gardenDefault = 'soil') =>
  modals.open({ title: bed ? `Edit ${bed.name}` : 'New bed', size: 'lg', children: <BedForm gardenId={gardenId} bed={bed} gardenDefault={gardenDefault} /> })

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
    <div className="sg-form">
      <TextInput label="Name" value={(v.name as string) ?? ''} onChange={(e) => set('name')(e.currentTarget.value)} required data-autofocus />
      <Select label="Crop" searchable data={(profiles ?? []).map((p) => ({ value: p.id, label: p.commonName }))} value={(v.profile as string) ?? null} onChange={set('profile')} />
      <TextInput label="Variety" value={(v.variety as string) ?? ''} onChange={(e) => set('variety')(e.currentTarget.value)} />
      <NumberInput label="Quantity" min={1} value={(v.quantity as number) ?? 1} onChange={set('quantity')} />
      <Select label="Stage" data={opts(stages)} value={v.stage as string} onChange={set('stage')} />
      <Select label="Started from" data={opts(['seed', 'transplant', 'cutting', 'unknown'])} value={v.startedFrom as string} onChange={set('startedFrom')} />
      <TextInput type="date" label="Planted" value={(v.plantedAt as string) ?? ''} onChange={(e) => set('plantedAt')(e.currentTarget.value)} />
      <TextInput type="date" label="Harvest from" value={(v.expectedHarvestStart as string) ?? ''} onChange={(e) => set('expectedHarvestStart')(e.currentTarget.value)} />
      <Textarea label="Notes" description="Anything the assistant should know about this plant" autosize minRows={2} value={(v.aiContext as string) ?? ''} onChange={(e) => set('aiContext')(e.currentTarget.value)} />
      <div className="sg-form-actions"><Button onClick={submit}>{plant ? 'Save plant' : 'Add plant'}</Button></div>
    </div>
  )
}

export const openPlantForm = (bedId: string, plant?: Plant) => modals.open({ title: plant ? `Edit ${plant.name}` : 'Add plant', size: 'lg', children: <PlantForm bedId={bedId} plant={plant} /> })
