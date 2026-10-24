import { ActionIcon, Button, Card, Grid, Group, NavLink, ScrollArea, Stack, Text } from '@mantine/core'
import { IconMessage, IconPlus, IconTrash } from '@tabler/icons-react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Conversation } from '../api/types'
import { ChatView } from '../components/ChatView'
import { ago } from '../format'

export function AssistantPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { data: conversations } = useQuery({ queryKey: ['conversations'], queryFn: () => api.get<Conversation[]>('/api/chat/conversations') })

  return (
    <Grid gap="md" style={{ height: 'calc(100vh - 100px)' }}>
      <Grid.Col span={{ base: 12, md: 3 }} h="100%">
        <Card h="100%" padding="xs">
          <Stack h="100%" gap="xs">
            <Button leftSection={<IconPlus size={16} />} onClick={() => navigate('/assistant')} variant="light" color="violet">New conversation</Button>
            <Text size="xs" c="dimmed" px={4}>Chat history is kept permanently; only recent messages are sent to the model.</Text>
            <ScrollArea style={{ flex: 1 }}>
              {(conversations ?? []).map((c) => (
                <NavLink
                  key={c.id}
                  active={c.id === id}
                  color="violet"
                  label={<Text size="sm" truncate>{c.title}</Text>}
                  description={ago(c.updatedAt)}
                  leftSection={<IconMessage size={16} />}
                  onClick={() => navigate(`/assistant/${c.id}`)}
                  rightSection={
                    <ActionIcon size="sm" variant="subtle" color="gray" onClick={async (e) => { e.stopPropagation(); await api.del(`/api/chat/conversations/${c.id}`); qc.invalidateQueries({ queryKey: ['conversations'] }); if (c.id === id) navigate('/assistant') }}>
                      <IconTrash size={14} />
                    </ActionIcon>
                  }
                  style={{ borderRadius: 8 }}
                />
              ))}
            </ScrollArea>
          </Stack>
        </Card>
      </Grid.Col>
      <Grid.Col span={{ base: 12, md: 9 }} h="100%">
        <Card h="100%" padding={0}>
          <Group px="md" py="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
            <Text fw={650}>{conversations?.find((c) => c.id === id)?.title ?? 'New conversation'}</Text>
          </Group>
          <div style={{ height: 'calc(100% - 45px)' }}>
            <ChatView key={id ?? 'new'} conversationId={id} onConversationCreated={(cid) => navigate(`/assistant/${cid}`, { replace: true })} />
          </div>
        </Card>
      </Grid.Col>
    </Grid>
  )
}
