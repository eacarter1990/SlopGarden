import { Badge, Box, Card, Group, NumberInput, Paper, Select, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core'
import { IconCameraOff } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { useLiveEvent } from '../api/live'
import type { Bed, CareRange, JsonSchema, LatestReading, Plant, ReservoirStatus } from '../api/types'
import { ago, fmtValue, metricLabel, readingTone } from '../format'

/** Live MJPEG feed. Width is the pixel budget for this view: small in a dashboard grid, large on a single plant. */
export function CameraImage({ cameraId, width = 480, alt, style }: { cameraId: string; width?: number; alt?: string; style?: React.CSSProperties }) {
  const [mode, setMode] = useState<'stream' | 'snapshot'>('stream')
  const [tick, setTick] = useState(() => Date.now())
  const [failed, setFailed] = useState(false)
  const [ready, setReady] = useState(false)
  const dpr = typeof window !== 'undefined' ? Math.min(window.devicePixelRatio || 1, 2) : 1
  const pixels = Math.round(width * dpr)
  useLiveEvent('cameraFrame', (e: { cameraId: string }) => {
    if (e.cameraId === cameraId && mode === 'snapshot') {
      setFailed(false)
      setReady(false)
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
          <IconCameraOff color="#8ea196" />
          <Text size="xs" c="gray.4">No frame yet</Text>
        </Stack>
      </Box>
    )
  const src = mode === 'stream' ? `/api/cameras/${cameraId}/stream?width=${pixels}` : `/api/cameras/${cameraId}/snapshot?width=${pixels}&t=${tick}`
  return (
    <div className="sg-camera-well" style={style}>
      {!ready && <div className="sg-camera-wait">Waiting for a frame</div>}
      <img className={ready ? 'sg-camera is-ready' : 'sg-camera'} src={src} alt={alt ?? 'camera'} onLoad={() => setReady(true)} onError={() => (mode === 'stream' ? setMode('snapshot') : setFailed(true))} />
    </div>
  )
}

export function ReadingPill({ r, target }: { r: LatestReading; target?: CareRange }) {
  const tone = readingTone(r.value, target?.min ?? r.alertLow, target?.max ?? r.alertHigh)
  const stale = r.at ? Date.now() - new Date(r.at).getTime() > 10 * 60_000 : true
  return (
    <Tooltip label={`${r.sensorName} · ${ago(r.at)}${target ? ` · target ${target.min ?? '…'}–${target.max ?? '…'} (${target.source})` : ''}`}>
      <Paper withBorder px={8} py={4} radius="md" className={`sg-reading sg-reading-${tone}`} style={{ opacity: stale ? 0.6 : 1, flexShrink: 0 }}>
        <Text size="10px" c="dimmed" tt="uppercase" fw={600} lh={1.2}>{metricLabel(r.metric)}</Text>
        <Group gap={4} wrap="nowrap">
          <span className="sg-dot" />
          <Text size="sm" fw={700}>{fmtValue(r.value, r.unit)}</Text>
        </Group>
      </Paper>
    </Tooltip>
  )
}

export function AiBadge({ active, label = 'Acting' }: { active: boolean; label?: string }) {
  if (!active) return null
  return (
    <span className="sg-acting-label">
      <span className="sg-live-dot" />
      {label}
    </span>
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
    <div className="sg-form">
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
    </div>
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
    <header className="sg-page-head">
      <div style={{ minWidth: 0 }}>
        <Text component="h1" className="sg-serif sg-page-title" fz={32} fw={560} lh={1.1} m={0}>{title}</Text>
        {subtitle && <Text c="dimmed" size="sm" mt={4} maw={640}>{subtitle}</Text>}
      </div>
      {right && <div className="sg-page-actions">{right}</div>}
    </header>
  )
}

/** A paper section: title, a plain-language hint, and the controls that belong with it. */
export function Section({ title, hint, action, children }: { title: string; hint?: React.ReactNode; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <Card className="sg-section">
      <div className="sg-section-head">
        <div>
          <Text className="sg-serif" fz={22} fw={560} lh={1.15}>{title}</Text>
          {hint && <Text size="sm" c="dimmed" mt={2}>{hint}</Text>}
        </div>
        {action}
      </div>
      {children}
    </Card>
  )
}

export function Empty({ children }: { children: React.ReactNode }) {
  return <div className="sg-empty">{children}</div>
}
