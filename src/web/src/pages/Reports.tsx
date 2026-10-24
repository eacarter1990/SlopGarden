import { Button, Card, Group, Image, SimpleGrid, Stack, Switch, Text, Title } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { Markdown } from '../components/Markdown'
import { PageTitle } from '../components/Widgets'
import { useGarden } from '../garden'

interface Prediction {
  plant: string
  bed?: string
  stage?: string
  summary: string
}
interface ReportImage {
  id: string
  camera: string
  role?: string
}
interface GrowthReport {
  id: string
  kind: string
  title: string
  body: string
  predictions: Prediction[]
  imageIds: ReportImage[]
  createdAt: string
}
interface ReportSchedule {
  id: string
  name: string
  enabled: boolean
  cadence?: string
}

export function ReportsPage() {
  const { gardenId, gardens } = useGarden()
  const qc = useQueryClient()
  const [open, setOpen] = useState<string | null>(null)
  const garden = gardens.find((g) => g.id === gardenId)
  const { data: reports } = useQuery({
    queryKey: ['reports', gardenId],
    enabled: !!gardenId,
    queryFn: () => api.get<GrowthReport[]>(`/api/gardens/${gardenId}/reports`),
  })
  const { data: schedules } = useQuery({
    queryKey: ['report-schedules', gardenId],
    enabled: !!gardenId,
    queryFn: () => api.get<ReportSchedule[]>(`/api/gardens/${gardenId}/reports/schedules`),
  })
  const generate = useMutation({
    mutationFn: () => api.post<GrowthReport>(`/api/gardens/${gardenId}/reports`, { kind: 'on_demand' }),
    onSuccess: (report) => {
      setOpen(report.id)
      qc.invalidateQueries({ queryKey: ['reports', gardenId] })
      notifications.show({ color: 'green', message: 'Report ready' })
    },
    onError: (e: Error) => notifications.show({ color: 'red', message: e.message }),
  })
  const toggle = useMutation({
    mutationFn: (cadence: string) => {
      const on = schedules?.some((s) => s.cadence === cadence && s.enabled)
      return api.post(`/api/gardens/${gardenId}/reports/schedule`, { cadence, enabled: !on })
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['report-schedules', gardenId] }),
    onError: (e: Error) => notifications.show({ color: 'red', message: e.message }),
  })
  const selected = reports?.find((r) => r.id === open) ?? reports?.[0]

  return (
    <Stack>
      <PageTitle title="Growth reports" subtitle={garden ? garden.name : 'Select a garden'} />
      <Card>
        <Group justify="space-between" align="flex-end">
          <Stack gap={6}>
            <Text fw={650}>Schedule</Text>
            <Text size="xs" c="dimmed">Daily at 07:00, Monday mornings, and the first of the month. Times use the garden's time zone.</Text>
            <Group>
              {(['daily', 'weekly', 'monthly'] as const).map((cadence) => (
                <Switch
                  key={cadence}
                  label={cadence}
                  checked={!!schedules?.some((s) => s.cadence === cadence && s.enabled)}
                  onChange={() => toggle.mutate(cadence)}
                />
              ))}
            </Group>
          </Stack>
          <Button loading={generate.isPending} onClick={() => generate.mutate()} disabled={!gardenId}>Generate now</Button>
        </Group>
      </Card>
      <Group align="flex-start" grow>
        <Card>
          <Stack gap="xs">
            {(reports ?? []).map((r) => (
              <Button key={r.id} variant={selected?.id === r.id ? 'light' : 'subtle'} justify="space-between" onClick={() => setOpen(r.id)}>
                <Group justify="space-between" wrap="nowrap" w="100%">
                  <Text size="sm" fw={600}>{r.title}</Text>
                  <Text size="xs" c="dimmed">{new Date(r.createdAt).toLocaleString()}</Text>
                </Group>
              </Button>
            ))}
            {reports?.length === 0 && <Text c="dimmed" size="sm">No reports yet.</Text>}
          </Stack>
        </Card>
        {selected && (
          <Card>
            <Title order={3}>{selected.title}</Title>
            <Text size="xs" c="dimmed" mb="sm">{selected.kind.replace('_', ' ')}</Text>
            {selected.imageIds?.length > 0 && (
              <SimpleGrid cols={{ base: 1, sm: 2 }} mb="md">
                {selected.imageIds.map((img) => (
                  <Image key={img.id} src={`/api/media/${img.id}`} alt={img.camera} radius="md" />
                ))}
              </SimpleGrid>
            )}
            <Markdown>{selected.body}</Markdown>
            {selected.predictions?.length > 0 && (
              <Stack gap={4} mt="md">
                <Text fw={650}>Harvest predictions</Text>
                {selected.predictions.map((p, i) => (
                  <Text key={i} size="sm"><b>{p.plant}</b>{p.bed ? ` · ${p.bed}` : ''}{p.stage ? ` · ${p.stage}` : ''}. {p.summary}</Text>
                ))}
              </Stack>
            )}
          </Card>
        )}
      </Group>
    </Stack>
  )
}
