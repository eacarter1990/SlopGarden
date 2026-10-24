import { Anchor, SegmentedControl, SimpleGrid, Stack, Text } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { Proposal } from '../api/types'
import { ProposalCard } from '../components/ProposalCard'
import { PageTitle } from '../components/Widgets'

export function ApprovalsPage() {
  const [status, setStatus] = useState('pending')
  const { data } = useQuery({ queryKey: ['proposals', status], queryFn: () => api.get<Proposal[]>(`/api/proposals/${status === 'all' ? '' : `?status=${status}`}`) })
  return (
    <Stack>
      <PageTitle
        title="Approvals"
        subtitle="Every change the AI (or an MCP client with a propose-scoped token) wants to make. Each states what and why."
        right={<SegmentedControl value={status} onChange={setStatus} data={[{ value: 'pending', label: 'Pending' }, { value: 'executed', label: 'Applied' }, { value: 'rejected', label: 'Rejected' }, { value: 'all', label: 'All' }]} />}
      />
      <SimpleGrid cols={{ base: 1, lg: 2 }}>
        {(data ?? []).map((p) => (
          <Stack key={p.id} gap={2}>
            <ProposalCard proposal={p} />
            {p.conversationId && <Anchor component={Link} to={`/assistant/${p.conversationId}`} size="xs" pl={4}>Open conversation</Anchor>}
          </Stack>
        ))}
      </SimpleGrid>
      {data?.length === 0 && <Text c="dimmed">{status === 'pending' ? 'No pending approvals.' : status === 'executed' ? 'No applied approvals.' : status === 'rejected' ? 'No rejected approvals.' : 'No approvals yet.'}</Text>}
    </Stack>
  )
}
