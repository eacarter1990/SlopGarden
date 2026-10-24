import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'

dayjs.extend(relativeTime)

export const metricLabels: Record<string, string> = {
  soil_moisture_pct: 'Soil moisture',
  soil_temperature_c: 'Soil temp',
  air_temperature_c: 'Air temp',
  air_humidity_pct: 'Humidity',
  air_pressure_hpa: 'Pressure',
  light_lux: 'Light',
  par_umol_m2_s: 'PAR',
  water_level_pct: 'Tank level',
  water_temperature_c: 'Water temp',
  ph: 'pH',
  ec_ms_cm: 'EC',
  co2_ppm: 'CO₂',
  rain_detected: 'Rain',
  flow_l_min: 'Flow',
  voltage_v: 'Voltage',
  digital: 'Digital',
}

export const metricLabel = (m: string) => metricLabels[m] ?? m.replace(/_/g, ' ')

export function fmtValue(v: number | null | undefined, unit?: string) {
  if (v === null || v === undefined) return '–'
  const digits = Math.abs(v) >= 1000 ? 0 : Math.abs(v) >= 100 ? 0 : 1
  const n = v.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 })
  return unit && unit !== 'bool' ? `${n}${unit.startsWith('%') || unit.startsWith('°') ? '' : ' '}${unit}` : n
}

export const ago = (t?: string | null) => (t ? dayjs(t).fromNow() : 'never')
export const when = (t?: string | null) => (t ? dayjs(t).format('MMM D, HH:mm') : '–')

export const titleCase = (s: string) => s.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())

export function readingTone(value: number | null | undefined, min?: number | null, max?: number | null): 'ok' | 'low' | 'high' | 'none' {
  if (value === null || value === undefined) return 'none'
  if (min !== null && min !== undefined && value < min) return 'low'
  if (max !== null && max !== undefined && value > max) return 'high'
  return 'ok'
}

export const toneColor = { ok: 'green', low: 'orange', high: 'blue', none: 'gray' } as const
