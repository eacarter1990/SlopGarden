import { ActionIcon, Badge, Box, Code, Collapse, Group, Paper, ScrollArea, Stack, Text, Textarea, Tooltip, UnstyledButton } from '@mantine/core'
import { IconArrowUp, IconChevronDown, IconChevronLeft, IconChevronRight, IconInfoCircle, IconTool } from '@tabler/icons-react'
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
  ['/', 'Dashboard'], ['/garden', 'Garden'], ['/plants', 'Plant'], ['/beds', 'Bed'], ['/reservoirs', 'Reservoir'],
  ['/sensors', 'Sensors'], ['/devices', 'Devices'], ['/reports', 'Reports'], ['/logs', 'Logs'],
  ['/automation', 'Automation'], ['/safety', 'Safety'], ['/assistant', 'Whole garden'],
  ['/approvals', 'Approvals'], ['/notifications', 'Notifications'], ['/settings', 'Settings'], ['/mcp-tools', 'MCP tools'],
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

/** Pending approvals stacked under the prompt box, one at a time, so they can be decided without scrolling the chat. */
function ApprovalQueue({ items, activeId, onSelect, onDecided }: { items: Proposal[]; activeId: string | null; onSelect: (id: string) => void; onDecided: (p: Proposal) => void }) {
  const index = Math.max(0, items.findIndex((p) => p.id === activeId))
  const active = items[index]
  return (
    <Paper className="sg-queue" withBorder radius="md" p="xs" mt={6}>
      <Group justify="space-between" mb={6} wrap="nowrap">
        <Text size="xs" fw={600}>{items.length === 1 ? '1 approval waiting' : `${items.length} approvals waiting`}</Text>
        <Group gap={2} wrap="nowrap">
          <ActionIcon size="sm" variant="subtle" color="gray" aria-label="Previous approval" disabled={index === 0} onClick={() => onSelect(items[index - 1].id)}>
            <IconChevronLeft size={14} />
          </ActionIcon>
          <Text size="xs" c="dimmed">{index + 1} of {items.length}</Text>
          <ActionIcon size="sm" variant="subtle" color="gray" aria-label="Next approval" disabled={index === items.length - 1} onClick={() => onSelect(items[index + 1].id)}>
            <IconChevronRight size={14} />
          </ActionIcon>
        </Group>
      </Group>
      <ScrollArea.Autosize mah="40vh">
        <ProposalCard key={active.id} proposal={active} onDecided={onDecided} />
      </ScrollArea.Autosize>
    </Paper>
  )
}

/** ChatGPT-style markdown chat with streaming, tool traces and approval prompts (inline, or queued under the prompt when several wait). */
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
  const atBottom = useRef(true)
  const [queuedIds, setQueuedIds] = useState<Set<string>>(new Set())
  const [activeQueued, setActiveQueued] = useState<string | null>(null)
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
    setQueuedIds(new Set())
    setActiveQueued(null)
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

  const pending = useMemo(
    () => visible.flatMap((m) => (m.role === 'tool' && m.proposalId && proposals[m.proposalId]?.status === 'pending' ? [proposals[m.proposalId]] : [])),
    [visible, proposals],
  )
  // Once several approvals queue up, the rest stay in the queue until decided instead of jumping back into the chat.
  const queued = pending.length > 1 ? pending : pending.filter((p) => queuedIds.has(p.id))
  const queuedKey = queued.map((p) => p.id).join(',')
  useEffect(() => {
    if (queuedKey) setQueuedIds((s) => (queuedKey.split(',').every((id) => s.has(id)) ? s : new Set([...s, ...queuedKey.split(',')])))
  }, [queuedKey])
  const decided = (p: Proposal) => setProposals((x) => ({ ...x, [p.id]: p }))

  useEffect(() => {
    if (atBottom.current) viewport.current?.scrollTo({ top: viewport.current.scrollHeight })
  }, [visible.length, streams, busy, queued.length])

  const onScroll = () => {
    const v = viewport.current
    if (v) atBottom.current = v.scrollHeight - v.scrollTop - v.clientHeight < 80
  }

  const send = async (text?: string) => {
    const content = (text ?? input).trim()
    if (!content || busy) return
    setInput('')
    setBusy(true)
    atBottom.current = true
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
      <ScrollArea style={{ flex: 1 }} viewportRef={viewport} onScrollPositionChange={onScroll} px={compact ? 'sm' : 'md'} py="md">
        <Stack gap="md">
          {visible.length === 0 && !busy && (
            <Stack gap="sm" py="md">
              <Text className="sg-serif" fz={22} fw={560}>Ask about what you're looking at.</Text>
              <Text size="sm" c="dimmed">Hi {user?.displayName?.split(' ')[0]}. I can read sensors, change targets, run valves and pumps, and set schedules. A change waits until you approve or reject it.</Text>
              <Stack gap={6}>
                {suggestions.map((s) => (
                  <UnstyledButton key={s} className="sg-suggest" onClick={() => send(s)}>{s}</UnstyledButton>
                ))}
              </Stack>
            </Stack>
          )}
          {visible.map((m) => {
            if (m.role === 'user')
              return (
                <Group key={m.id} justify="flex-end" align="flex-start" gap="xs" wrap="nowrap">
                  <Paper className="sg-you" px="md" py={8} radius="lg" maw="85%">
                    <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{m.content}</Text>
                  </Paper>
                </Group>
              )
            if (m.role === 'tool') {
              const p = m.proposalId ? proposals[m.proposalId] : undefined
              const inQueue = !!p && queued.some((q) => q.id === p.id)
              return (
                <Box key={m.id} pl={compact ? 0 : 40}>
                  {inQueue ? (
                    <UnstyledButton onClick={() => setActiveQueued(p.id)} w="100%">
                      <Paper withBorder radius="md" px="sm" py={6}>
                        <Group gap={6} wrap="nowrap">
                          <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>{p.title}</Text>
                          <Badge size="xs" variant="light" color="red" style={{ flex: '0 0 auto' }}>waiting below</Badge>
                        </Group>
                      </Paper>
                    </UnstyledButton>
                  ) : p ? <ProposalCard proposal={p} onDecided={decided} /> : <ToolChip m={m} />}
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
              <Box key={m.id} style={{ minWidth: 0 }}>
                {m.content && <Markdown>{m.content}</Markdown>}
                {m.error && m.error !== 'not_configured' && <Text size="xs" c="red">{m.error}</Text>}
              </Box>
            )
          })}
          {Object.entries(streams).map(([id, text]) => (
            <Box key={id} style={{ minWidth: 0 }}><Markdown>{text}</Markdown></Box>
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
          <Tooltip label="The assistant uses the page you're on as default context, and can still reach the rest of the garden">
            <Text className="sg-context">Looking at {contextLabel}</Text>
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
          <ActionIcon className="sg-send" size={36} radius="xl" variant="filled" onClick={() => send()} disabled={!input.trim() || busy} aria-label="Send">
            <IconArrowUp size={18} />
          </ActionIcon>
        </Group>
        {queued.length > 0 && <ApprovalQueue items={queued} activeId={activeQueued} onSelect={setActiveQueued} onDecided={decided} />}
      </Box>
    </Stack>
  )
}
