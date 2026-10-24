import { LineChart } from '@mantine/charts'
import { Group, SegmentedControl, Skeleton, Stack, Text } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import dayjs from 'dayjs'
import { useState } from 'react'
import { api } from '../api/client'
import type { ChannelSeries } from '../api/types'
import { metricLabel } from '../format'

const colors = ['green.6', 'blue.6', 'orange.6', 'grape.6', 'teal.6', 'red.6']

/** History of one sensor's channels, bucketed server-side. */
export function ReadingsChart({ sensorId, channelId, height = 200, target }: { sensorId?: string; channelId?: string; height?: number; target?: { min?: number | null; max?: number | null } }) {
  const [hours, setHours] = useState('6')
  const { data, isLoading } = useQuery({
    queryKey: ['readings', sensorId, channelId, hours],
    queryFn: () => api.op<ChannelSeries[]>('get_readings', { sensorId, channelId, hours: Number(hours) }),
    refetchInterval: 30_000,
  })
  if (isLoading) return <Skeleton h={height} />
  const series = data ?? []
  const rows = new Map<string, Record<string, number | string>>()
  series.forEach((s) =>
    s.points.forEach((p) => {
      const key = p.time
      const row = rows.get(key) ?? { time: key }
      row[s.metric] = Math.round(p.avg * 10) / 10
      rows.set(key, row)
    }),
  )
  const chartData = [...rows.values()].sort((a, b) => String(a.time).localeCompare(String(b.time))).map((r) => ({ ...r, label: dayjs(r.time as string).format(Number(hours) > 24 ? 'MMM D HH:mm' : 'HH:mm') }))
  const refs = [
    ...(target?.min != null ? [{ y: target.min, label: `min ${target.min}`, color: 'orange.5' }] : []),
    ...(target?.max != null ? [{ y: target.max, label: `max ${target.max}`, color: 'blue.4' }] : []),
  ]
  return (
    <Stack gap={4}>
      <Group justify="space-between">
        <Text size="xs" c="dimmed">{series.map((s) => `${metricLabel(s.metric)} (${s.unit})`).join(' · ')}{series[0] ? ` · ${series[0].bucketMinutes} min buckets` : ''}</Text>
        <SegmentedControl size="xs" value={hours} onChange={setHours} data={[{ value: '1', label: '1h' }, { value: '6', label: '6h' }, { value: '24', label: '24h' }, { value: '168', label: '7d' }]} />
      </Group>
      {chartData.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="lg">No readings in this period yet.</Text>
      ) : (
        <LineChart
          h={height}
          data={chartData}
          dataKey="label"
          series={series.map((s, i) => ({ name: s.metric, label: metricLabel(s.metric), color: colors[i % colors.length] }))}
          curveType="monotone"
          withDots={false}
          referenceLines={refs}
          gridAxis="y"
          tickLine="none"
        />
      )}
    </Stack>
  )
}
