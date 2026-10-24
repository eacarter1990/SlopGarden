import { Card, Code, Select, Stack, Text } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { PageTitle } from '../components/Widgets'

interface Scopes {
  gardens: { id: string; name: string }[]
  beds: { id: string; name: string; gardenId: string }[]
  plants: { id: string; name: string; bedId: string }[]
  devices: { id: string; name: string }[]
}

const scopes = [
  { value: 'server', label: 'Server application' },
  { value: 'garden', label: 'Garden activity' },
  { value: 'bed', label: 'Bed activity' },
  { value: 'plant', label: 'Plant activity' },
  { value: 'client', label: 'Client application' },
]

export function LogsPage() {
  const [scope, setScope] = useState('server')
  const [id, setId] = useState<string | null>(null)
  const { data: lists } = useQuery({ queryKey: ['log-scopes'], queryFn: () => api.get<Scopes>('/api/logs/scopes') })
  const choices = scope === 'garden' ? lists?.gardens : scope === 'bed' ? lists?.beds : scope === 'plant' ? lists?.plants : scope === 'client' ? lists?.devices : []
  const selected = scope === 'server' ? 'server' : id
  const { data, isFetching } = useQuery({
    queryKey: ['log', scope, selected],
    enabled: scope === 'server' || !!id,
    queryFn: () => api.get<{ text: string; title?: string }>(`/api/logs?scope=${scope}&id=${id ?? ''}&lines=300`),
    refetchInterval: 10_000,
  })

  return (
    <Stack>
      <PageTitle title="Logs" subtitle="Activity logs for each garden, bed and plant, plus the server log and one client log per device. Files stay on the server's data volume." />
      <Card>
        <Stack>
          <Select label="Log" data={scopes} value={scope} onChange={(v) => { setScope(v ?? 'server'); setId(null) }} allowDeselect={false} />
          {scope !== 'server' && (
            <Select
              label={scope}
              placeholder="Choose one"
              data={(choices ?? []).map((c) => ({ value: c.id, label: c.name }))}
              value={id}
              onChange={setId}
              searchable
            />
          )}
          <Text size="xs" c="dimmed">{isFetching ? 'Refreshing…' : 'Refreshes every 10 seconds'}</Text>
          <Code block style={{ whiteSpace: 'pre-wrap', maxHeight: 560, overflow: 'auto' }}>
            {data?.text || 'Nothing here yet.'}
          </Code>
        </Stack>
      </Card>
    </Stack>
  )
}
