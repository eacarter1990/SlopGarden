import { ActionIcon, Group, Select, Stack, Text, Tooltip } from '@mantine/core'
import { IconArrowsMaximize, IconPlus, IconX } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { Conversation } from '../api/types'
import { ChatView } from './ChatView'

/** Side-panel assistant available on every page, and in the walk on a phone. */
export function ChatDrawer({ onClose, embedded }: { onClose?: () => void; embedded?: boolean }) {
  const [convId, setConvId] = useState<string | undefined>()
  const navigate = useNavigate()
  const { data: conversations } = useQuery({ queryKey: ['conversations'], queryFn: () => api.get<Conversation[]>('/api/chat/conversations') })

  return (
    <Stack gap={0} h="100%">
      <Group className="sg-drawer-head" justify="space-between" px="sm" py={8} wrap="nowrap">
        <Text className="sg-serif" fw={560} size="lg">Assistant</Text>
        <Group gap={4} wrap="nowrap">
          <Select
            size="xs"
            w={190}
            placeholder="Conversation"
            data={(conversations ?? []).map((c) => ({ value: c.id, label: c.title }))}
            value={convId ?? null}
            onChange={(v) => setConvId(v ?? undefined)}
            clearable
            searchable
          />
          <Tooltip label="New conversation">
            <ActionIcon variant="subtle" onClick={() => setConvId(undefined)}><IconPlus size={16} /></ActionIcon>
          </Tooltip>
          <Tooltip label="Open full page">
            <ActionIcon variant="subtle" onClick={() => navigate(convId ? `/assistant/${convId}` : '/assistant')}><IconArrowsMaximize size={16} /></ActionIcon>
          </Tooltip>
          {!embedded && onClose && <ActionIcon variant="subtle" onClick={onClose} aria-label="Close assistant"><IconX size={16} /></ActionIcon>}
        </Group>
      </Group>
      <div style={{ flex: 1, minHeight: 0 }}>
        <ChatView conversationId={convId} onConversationCreated={setConvId} compact />
      </div>
    </Stack>
  )
}
