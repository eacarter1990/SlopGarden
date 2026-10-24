import { ActionIcon, Avatar, Badge, Box, Code, Collapse, Group, Paper, ScrollArea, Stack, Text, Textarea, Tooltip, UnstyledButton } from '@mantine/core'
import { IconArrowUp, IconChevronDown, IconChevronRight, IconInfoCircle, IconRobot, IconTool, IconUser } from '@tabler/icons-react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api/client'
import { useLiveEvent } from '../api/live'
import type { ChatMessage, Conversation, Proposal } from '../api/types'
import { useAuth } from '../auth'
import { usePageContext } from '../pageContext'
import { Markdown } from './Markdown'
import { ProposalCard } from './ProposalCard'

interface ChatEvent {
  type: 'delta' | 'message' | 'done'
  conversationId: string
  streamId?: string
  text?: string
  message?: ChatMessage
}

const routeNames: [string, string][] = [
  ['/', 'Dashboard'], ['/garden', 'Garden'], ['/sensors', 'Sensors'], ['/devices', 'Devices'], ['/automation', 'Automation'], ['/safety', 'Safety'],
  ['/assistant', 'Whole garden'], ['/approvals', 'Approvals'], ['/notifications', 'Notifications'], ['/settings', 'Settings'], ['/mcp-tools', 'MCP tools'],
]

const suggestions = ['How is the garden doing?', 'Is anything too dry?', 'Water the tomatoes every morning', 'Show me the tomato bed camera']

function ToolChip({ m }: { m: ChatMessage }) {
  const [open, setOpen] = useState(false)
  let pretty = m.content
  try {
    pretty = JSON.stringify(JSON.parse(m.content), null, 2)
  } catch {
    /* not JSON */
  }
  return (
    <Box>
      <UnstyledButton onClick={() => setOpen((o) => !o)}>
        <Group gap={4}>
          {open ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
          <IconTool size={12} />
          <Text size="xs" c={m.error ? 'red' : 'dimmed'} ff="monospace">
            {m.toolName}
            {m.error ? ` · ${m.error}` : ''}
          </Text>
        </Group>
      </UnstyledButton>
      <Collapse expanded={open}>
        <Code block mah={240} style={{ overflow: 'auto', fontSize: 11 }}>{pretty.length > 6000 ? pretty.slice(0, 6000) + '\n…' : pretty}</Code>
      </Collapse>
    </Box>
  )
}

/** ChatGPT-style markdown chat with streaming, tool traces and inline approval prompts. */
export function ChatView({ conversationId, onConversationCreated, compact }: { conversationId?: string; onConversationCreated?: (id: string) => void; compact?: boolean }) {
  const qc = useQueryClient()
  const { page } = usePageContext()
  const { user } = useAuth()
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [streams, setStreams] = useState<Record<string, string>>({})
  const [localMessages, setLocalMessages] = useState<ChatMessage[]>([])
  const [proposals, setProposals] = useState<Record<string, Proposal>>({})
  const viewport = useRef<HTMLDivElement>(null)
  const [convId, setConvId] = useState(conversationId)
  useEffect(() => setConvId(conversationId), [conversationId])

  const { data } = useQuery({
    queryKey: ['chat', convId],
    queryFn: () => api.get<{ messages: ChatMessage[]; proposals: Proposal[] }>(`/api/chat/conversations/${convId}/messages`),
    enabled: !!convId,
  })

  useEffect(() => {
    if (!data) return
    setLocalMessages(data.messages)
    setProposals((p) => ({ ...p, ...Object.fromEntries(data.proposals.map((x) => [x.id, x])) }))
  }, [data])

  useEffect(() => {
    if (!convId) {
      setLocalMessages([])
      setStreams({})
    }
  }, [convId])

  useLiveEvent('chat', (e: ChatEvent) => {
    if (e.conversationId !== convId) return
    if (e.type === 'delta' && e.streamId) setStreams((s) => ({ ...s, [e.streamId!]: (s[e.streamId!] ?? '') + (e.text ?? '') }))
    if (e.type === 'message' && e.message) {
      const msg = e.message
      setLocalMessages((ms) => (ms.some((m) => m.id === msg.id) ? ms : [...ms, msg].sort((a, b) => a.seq - b.seq)))
      if (e.streamId) setStreams((s) => {
        const { [e.streamId!]: _, ...rest } = s
        return rest
      })
      if (msg.proposalId && !proposals[msg.proposalId]) qc.invalidateQueries({ queryKey: ['chat', convId] })
    }
    if (e.type === 'done') {
      setBusy(false)
      setStreams({})
      qc.invalidateQueries({ queryKey: ['chat', convId] })
      qc.invalidateQueries({ queryKey: ['conversations'] })
    }
  })

  useLiveEvent('proposal', (e: { proposal: Proposal }) => {
    if (e.proposal.conversationId === convId) setProposals((p) => ({ ...p, [e.proposal.id]: e.proposal }))
  })

  const visible = useMemo(() => localMessages.filter((m) => !(m.role === 'assistant' && !m.content && !m.error)), [localMessages])

  useEffect(() => {
    viewport.current?.scrollTo({ top: viewport.current.scrollHeight, behavior: 'smooth' })
  }, [visible.length, streams, busy])

  const send = async (text?: string) => {
    const content = (text ?? input).trim()
    if (!content || busy) return
    setInput('')
    setBusy(true)
    try {
      let id = convId
      if (!id) {
        const c = await api.post<Conversation>('/api/chat/conversations', {})
        id = c.id
        setConvId(id)
        onConversationCreated?.(id)
      }
      const msg = await api.post<ChatMessage>(`/api/chat/conversations/${id}/messages`, {
        content,
        page: { route: page.route, entityType: page.entityType ?? null, entityId: page.entityId ?? null, label: page.label ?? null },
      })
      setLocalMessages((ms) => (ms.some((m) => m.id === msg.id) ? ms : [...ms, msg]))
      qc.invalidateQueries({ queryKey: ['conversations'] })
    } catch (e) {
      setBusy(false)
      setLocalMessages((ms) => [...ms, { id: crypto.randomUUID(), conversationId: convId ?? '', seq: 1e9, role: 'event', content: `⚠️ ${(e as Error).message}`, createdAt: new Date().toISOString() }])
    }
  }

  const contextLabel = page.label ?? routeNames.find(([prefix]) => page.route === prefix || (prefix !== '/' && page.route.startsWith(prefix)))?.[1] ?? page.route

  return (
    <Stack gap={0} h="100%" style={{ minHeight: 0 }}>
      <ScrollArea style={{ flex: 1 }} viewportRef={viewport} px={compact ? 'sm' : 'md'} py="md">
        <Stack gap="md">
          {visible.length === 0 && !busy && (
            <Stack gap="xs" align="center" py="xl">
              <Avatar color="violet" radius="xl" size="lg"><IconRobot /></Avatar>
              <Text fw={600}>Hi {user?.displayName?.split(' ')[0]}, I'm your garden assistant.</Text>
              <Text size="sm" c="dimmed" ta="center" maw={360}>I can read every sensor, tune plant targets, run valves and pumps, and set up schedules. Any change I suggest waits for your approval.</Text>
              <Group gap={6} justify="center" mt="xs">
                {suggestions.map((s) => (
                  <Badge key={s} variant="light" color="violet" size="lg" style={{ cursor: 'pointer', textTransform: 'none', fontWeight: 500 }} onClick={() => send(s)}>{s}</Badge>
                ))}
              </Group>
            </Stack>
          )}
          {visible.map((m) => {
            if (m.role === 'user')
              return (
                <Group key={m.id} justify="flex-end" align="flex-start" gap="xs" wrap="nowrap">
                  <Paper bg="green.7" c="white" px="md" py={8} radius="lg" maw="85%" style={{ borderBottomRightRadius: 4 }}>
                    <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{m.content}</Text>
                  </Paper>
                  {!compact && <Avatar size="sm" radius="xl" color="green"><IconUser size={16} /></Avatar>}
                </Group>
              )
            if (m.role === 'tool') {
              const p = m.proposalId ? proposals[m.proposalId] : undefined
              return (
                <Box key={m.id} pl={compact ? 0 : 40}>
                  {p ? <ProposalCard proposal={p} onDecided={(np) => setProposals((x) => ({ ...x, [np.id]: np }))} /> : <ToolChip m={m} />}
                </Box>
              )
            }
            if (m.role === 'event')
              return (
                <Group key={m.id} gap={6} justify="center" wrap="nowrap">
                  <IconInfoCircle size={14} color="var(--mantine-color-dimmed)" />
                  <Box c="dimmed" style={{ fontSize: 12 }}><Markdown>{m.content}</Markdown></Box>
                </Group>
              )
            return (
              <Group key={m.id} align="flex-start" gap="xs" wrap="nowrap">
                {!compact && <Avatar size="sm" radius="xl" color="violet"><IconRobot size={16} /></Avatar>}
                <Box style={{ flex: 1, minWidth: 0 }}>
                  {m.content && <Markdown>{m.content}</Markdown>}
                  {m.error && m.error !== 'not_configured' && <Text size="xs" c="red">{m.error}</Text>}
                </Box>
              </Group>
            )
          })}
          {Object.entries(streams).map(([id, text]) => (
            <Group key={id} align="flex-start" gap="xs" wrap="nowrap">
              {!compact && <Avatar size="sm" radius="xl" color="violet"><IconRobot size={16} /></Avatar>}
              <Box style={{ flex: 1, minWidth: 0 }}><Markdown>{text}</Markdown></Box>
            </Group>
          ))}
          {busy && Object.keys(streams).length === 0 && (
            <Group gap="xs" pl={compact ? 0 : 40}>
              <span className="sg-typing"><span /><span /><span /></span>
              <Text size="xs" c="dimmed">thinking…</Text>
            </Group>
          )}
        </Stack>
      </ScrollArea>
      <Box p="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <Group gap={6} mb={6}>
          <Tooltip label="The assistant uses the page you're on as default context">
            <Badge size="xs" variant="dot" color="violet" style={{ textTransform: 'none' }}>Context: {contextLabel}</Badge>
          </Tooltip>
        </Group>
        <Group align="flex-end" gap="xs" wrap="nowrap">
          <Textarea
            style={{ flex: 1 }}
            autosize
            minRows={1}
            maxRows={6}
            radius="lg"
            placeholder="Ask about your plants, or tell the AI what to do…"
            value={input}
            onChange={(e) => setInput(e.currentTarget.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault()
                send()
              }
            }}
          />
          <ActionIcon size={36} radius="xl" color="violet" onClick={() => send()} disabled={!input.trim() || busy} aria-label="Send">
            <IconArrowUp size={18} />
          </ActionIcon>
        </Group>
      </Box>
    </Stack>
  )
}
