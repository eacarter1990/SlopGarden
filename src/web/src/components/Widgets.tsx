import { Badge, Box, Group, NumberInput, Paper, Select, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core'
import { IconCameraOff, IconSparkles } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { useLiveEvent } from '../api/live'
import type { Bed, CareRange, JsonSchema, LatestReading, Plant, ReservoirStatus } from '../api/types'
import { ago, fmtValue, metricLabel, readingTone, toneColor } from '../format'

/** Live MJPEG feed. Width is the pixel budget for this view: small in a dashboard grid, large on a single plant. */
export function CameraImage({ cameraId, width = 480, alt, style }: { cameraId: string; width?: number; alt?: string; style?: React.CSSProperties }) {
  const [mode, setMode] = useState<'stream' | 'snapshot'>('stream')
  const [tick, setTick] = useState(() => Date.now())
  const [failed, setFailed] = useState(false)
  const dpr = typeof window !== 'undefined' ? Math.min(window.devicePixelRatio || 1, 2) : 1
  const pixels = Math.round(width * dpr)
  useLiveEvent('cameraFrame', (e: { cameraId: string }) => {
    if (e.cameraId === cameraId && mode === 'snapshot') {
      setFailed(false)
      setTick(Date.now())
    }
  })
  useEffect(() => {
    if (mode !== 'snapshot') return
    const t = window.setInterval(() => setTick(Date.now()), 30_000)
    return () => window.clearInterval(t)
  }, [mode])
  if (failed)
    return (
      <Box className="sg-camera" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', ...style }}>
        <Stack gap={4} align="center">
          <IconCameraOff color="var(--mantine-color-gray-5)" />
          <Text size="xs" c="gray.5">No frame yet</Text>
        </Stack>
      </Box>
    )
  const src = mode === 'stream' ? `/api/cameras/${cameraId}/stream?width=${pixels}` : `/api/cameras/${cameraId}/snapshot?width=${pixels}&t=${tick}`
  return <img className="sg-camera" style={style} src={src} alt={alt ?? 'camera'} onError={() => (mode === 'stream' ? setMode('snapshot') : setFailed(true))} />
}

export function ReadingPill({ r, target }: { r: LatestReading; target?: CareRange }) {
  const tone = readingTone(r.value, target?.min ?? r.alertLow, target?.max ?? r.alertHigh)
  const stale = r.at ? Date.now() - new Date(r.at).getTime() > 10 * 60_000 : true
  return (
    <Tooltip label={`${r.sensorName} · ${ago(r.at)}${target ? ` · target ${target.min ?? '…'}–${target.max ?? '…'} (${target.source})` : ''}`}>
      <Paper withBorder px={8} py={4} radius="md" style={{ opacity: stale ? 0.6 : 1 }}>
        <Text size="10px" c="dimmed" tt="uppercase" fw={600} lh={1.2}>{metricLabel(r.metric)}</Text>
        <Group gap={4} wrap="nowrap">
          <span className="sg-dot" style={{ background: `var(--mantine-color-${toneColor[tone]}-6)` }} />
          <Text size="sm" fw={700}>{fmtValue(r.value, r.unit)}</Text>
        </Group>
      </Paper>
    </Tooltip>
  )
}

export function AiBadge({ active, label = 'AI acting' }: { active: boolean; label?: string }) {
  if (!active) return null
  return (
    <Badge color="violet" variant="filled" leftSection={<IconSparkles size={12} />} size="sm">
      {label}
    </Badge>
  )
}

export function TrustBadge({ trust }: { trust: string }) {
  const map: Record<string, [string, string, string]> = {
    builtin: ['gray', 'Built-in', 'Compiled into the SlopGarden client'],
    signed: ['green', 'Signed', 'Signature verified against a trusted catalog key'],
    unsigned: ['orange', 'Unsigned', 'This plugin is not signed. Its security cannot be guaranteed.'],
    invalid: ['red', 'Invalid signature', 'Signature does not match — the client refused to load it'],
  }
  const [color, label, tip] = map[trust] ?? ['gray', trust, '']
  return (
    <Tooltip label={tip} multiline w={260}>
      <Badge color={color} variant="light">{label}</Badge>
    </Tooltip>
  )
}

/** Renders a plugin's JSON-Schema settings as a form (strings, numbers, booleans, enums). */
export function SchemaForm({ schema, value, onChange }: { schema?: JsonSchema; value: Record<string, unknown>; onChange: (v: Record<string, unknown>) => void }) {
  const props = schema?.properties ?? {}
  const keys = Object.keys(props)
  if (keys.length === 0) return <Text size="xs" c="dimmed">This plugin has no settings.</Text>
  const set = (k: string, v: unknown) => onChange({ ...value, [k]: v })
  return (
    <Stack gap="xs">
      {keys.map((k) => {
        const p = props[k]
        const type = Array.isArray(p.type) ? p.type.find((t) => t !== 'null') : p.type
        const required = schema?.required?.includes(k)
        const label = `${k}${required ? ' *' : ''}`
        if (p.enum) return <Select key={k} label={label} description={p.description} data={p.enum} value={(value[k] as string) ?? (p.default as string) ?? null} onChange={(v) => set(k, v)} />
        if (type === 'boolean') return <Switch key={k} label={k} description={p.description} checked={(value[k] as boolean) ?? (p.default as boolean) ?? false} onChange={(e) => set(k, e.currentTarget.checked)} />
        if (type === 'integer' || type === 'number')
          return <NumberInput key={k} label={label} description={p.description} value={(value[k] as number) ?? (p.default as number) ?? ''} onChange={(v) => set(k, v === '' ? undefined : Number(v))} />
        return <TextInput key={k} label={label} description={p.description} value={(value[k] as string) ?? ''} placeholder={p.default !== undefined ? String(p.default) : undefined} onChange={(e) => set(k, e.currentTarget.value)} />
      })}
    </Stack>
  )
}

export interface TargetValue {
  targetType: string
  bedId?: string | null
  plantId?: string | null
  reservoirId?: string | null
}

/** Sensors, actuators and cameras target the whole garden, a bed, or a single plant. */
export function TargetPicker({ value, onChange }: { value: TargetValue; onChange: (v: TargetValue) => void }) {
  const { data: beds } = useQuery({ queryKey: ['beds'], queryFn: () => api.op<Bed[]>('list_beds') })
  const { data: plants } = useQuery({ queryKey: ['plants'], queryFn: () => api.op<Plant[]>('list_plants') })
  const { data: reservoirs } = useQuery({ queryKey: ['reservoirs'], queryFn: () => api.op<ReservoirStatus[]>('list_reservoirs') })
  const bedName = (id: string) => beds?.find((b) => b.id === id)?.name ?? ''
  return (
    <Group grow align="flex-start">
      <Select
        label="Targets"
        data={[{ value: 'garden', label: 'Whole garden' }, { value: 'bed', label: 'A bed' }, { value: 'plant', label: 'A single plant' }, { value: 'reservoir', label: 'A reservoir (hydroponics)' }]}
        value={value.targetType}
        onChange={(t) => onChange({ targetType: t ?? 'garden', bedId: null, plantId: null, reservoirId: null })}
        allowDeselect={false}
      />
      {value.targetType === 'bed' && (
        <Select label="Bed" data={(beds ?? []).map((b) => ({ value: b.id, label: b.name }))} value={value.bedId ?? null} onChange={(b) => onChange({ ...value, bedId: b })} searchable />
      )}
      {value.targetType === 'reservoir' && (
        <Select label="Reservoir" data={(reservoirs ?? []).map((r) => ({ value: r.id, label: r.name }))} value={value.reservoirId ?? null} onChange={(r) => onChange({ ...value, reservoirId: r })} searchable />
      )}
      {value.targetType === 'plant' && (
        <Select
          label="Plant"
          data={(plants ?? []).map((p) => ({ value: p.id, label: `${p.name} (${bedName(p.bedId)})` }))}
          value={value.plantId ?? null}
          onChange={(p) => onChange({ ...value, plantId: p })}
          searchable
        />
      )}
    </Group>
  )
}

export function PageTitle({ title, subtitle, right }: { title: string; subtitle?: React.ReactNode; right?: React.ReactNode }) {
  return (
    <Group justify="space-between" mb="lg" align="flex-end" wrap="wrap">
      <div>
        <Text component="h1" fz={26} fw={750} m={0}>{title}</Text>
        {subtitle && <Text c="dimmed" size="sm">{subtitle}</Text>}
      </div>
      {right && <Group gap="xs">{right}</Group>}
    </Group>
  )
}
