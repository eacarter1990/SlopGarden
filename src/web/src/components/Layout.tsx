import { ActionIcon, AppShell, Badge, Burger, Group, Indicator, Menu, NavLink, ScrollArea, Select, Tooltip, UnstyledButton } from '@mantine/core'
import { useDisclosure, useMediaQuery } from '@mantine/hooks'
import {
  IconApi,
  IconBell,
  IconCalendarTime,
  IconCpu,
  IconGauge,
  IconLayoutDashboard,
  IconLogout,
  IconMessageChatbot,
  IconUser,
  IconPlant2,
  IconSettings,
  IconShieldCheck,
  IconChecklist,
  IconNotes,
  IconReport,
} from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { api } from '../api/client'
import { useLive } from '../api/live'
import type { GardenOverview } from '../api/types'
import { roleLabel, useAuth } from '../auth'
import { GardenProvider, useGarden } from '../garden'
import { ChatDrawer } from './ChatDrawer'
import { EmergencyStopButton } from './EmergencyStopButton'

const groups = [
  {
    label: 'The garden',
    items: [
      { to: '/', label: 'Dashboard', icon: IconLayoutDashboard },
      { to: '/garden', label: 'Garden', icon: IconPlant2 },
      { to: '/sensors', label: 'Sensors & readings', icon: IconGauge },
    ],
  },
  {
    label: 'Operations',
    items: [
      { to: '/devices', label: 'Devices', icon: IconCpu },
      { to: '/automation', label: 'Automation', icon: IconCalendarTime },
      { to: '/safety', label: 'Safety', icon: IconShieldCheck },
      { to: '/reports', label: 'Reports', icon: IconReport },
      { to: '/logs', label: 'Logs', icon: IconNotes },
      { to: '/approvals', label: 'Approvals', icon: IconChecklist },
    ],
  },
  {
    label: 'Server',
    items: [
      { to: '/notifications', label: 'Notifications', icon: IconBell },
      { to: '/assistant', label: 'Assistant', icon: IconMessageChatbot },
      { to: '/mcp-tools', label: 'MCP tools', icon: IconApi },
      { to: '/settings', label: 'Settings', icon: IconSettings },
    ],
  },
]

function isCompanion(path: string) {
  return path === '/' || path === '/garden' || path.startsWith('/plants/') || path.startsWith('/beds/') || path.startsWith('/reservoirs/')
}

export function useOverview() {
  const { gardenId } = useGarden()
  return useQuery({
    queryKey: ['overview', gardenId || 'default'],
    queryFn: () => api.op<GardenOverview>('get_garden_overview', gardenId ? { gardenId } : {}),
    refetchInterval: 30_000,
  })
}

export function Layout({ children }: { children: ReactNode }) {
  return (
    <GardenProvider>
      <LayoutShell>{children}</LayoutShell>
    </GardenProvider>
  )
}

function LayoutShell({ children }: { children: ReactNode }) {
  const [opened, { toggle, close }] = useDisclosure()
  const { gardens, gardenId, select } = useGarden()
  const [chatOpen, chat] = useDisclosure(false)
  const [dismissedPath, setDismissedPath] = useState<string | null>(null)
  const location = useLocation()
  const { user, logout } = useAuth()
  const { connected } = useLive()
  const { data: overview } = useOverview()
  const desktop = useMediaQuery('(min-width: 62em)', false, { getInitialValueInEffect: false })
  const wide = useMediaQuery('(min-width: 1400px)', false, { getInitialValueInEffect: false })
  const onAssistantPage = location.pathname.startsWith('/assistant')
  const companion = isCompanion(location.pathname)
  const dismissed = dismissedPath === location.pathname
  const showAside = !!desktop && !onAssistantPage && ((companion && !dismissed) || chatOpen)

  const badge = (to: string) => {
    if (to === '/approvals' && overview?.pendingProposals) return <Badge size="xs" color="red" circle>{overview.pendingProposals}</Badge>
    if (to === '/devices' && overview?.devices.pending) return <Badge size="xs" color="red" circle>{overview.devices.pending}</Badge>
    if (to === '/notifications' && overview?.unreadNotifications) return <Badge size="xs" color="gray" variant="light">{overview.unreadNotifications > 99 ? '99+' : overview.unreadNotifications}</Badge>
    return null
  }

  const tabActive = (to: string) => (to === '/' ? location.pathname === '/' : location.pathname.startsWith(to))

  return (
    <AppShell
      className="sg-shell"
      header={{ height: { base: 108, sm: 64 } }}
      navbar={{ width: 232, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      aside={{ width: wide ? 420 : 380, breakpoint: 'md', collapsed: { desktop: !showAside, mobile: true } }}
      padding={{ base: 'sm', sm: 'lg' }}
    >
      <AppShell.Header>
        <div style={{ height: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', gap: 6 }}>
          <Group h={{ base: 'auto', sm: '100%' }} px={{ base: 'xs', sm: 'md' }} justify="space-between" wrap="nowrap" gap="xs" style={{ minWidth: 0 }}>
            <Group gap="xs" wrap="nowrap" style={{ minWidth: 0, flex: '1 1 auto' }}>
              <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label="Menu" />
              <Link to="/" className="sg-mark">SlopGarden</Link>
              {gardens.length > 0 && (
                <Select
                  aria-label="Garden"
                  data={gardens.map((g) => ({ value: g.id, label: g.name }))}
                  value={gardenId || null}
                  onChange={(v) => v && select(v)}
                  allowDeselect={false}
                  searchable
                  size="sm"
                  visibleFrom="sm"
                  w={220}
                />
              )}
            </Group>
            <Group gap={6} wrap="nowrap" style={{ flex: '0 0 auto' }}>
              <Tooltip label={connected ? 'Live updates connected' : 'Reconnecting…'}>
                <span className="sg-dot" style={{ background: connected ? 'var(--sg-moss)' : 'var(--sg-accent)' }} />
              </Tooltip>
              <EmergencyStopButton scopeType="global" label="Stop" compact />
              {!onAssistantPage && (
                <Tooltip label="Assistant">
                  <Indicator disabled={!overview?.runningAiActivities.length} color="red" processing size={8}>
                    <ActionIcon
                      className="sg-assistant-toggle"
                      variant="subtle"
                      size="lg"
                      onClick={() => (showAside ? (chat.close(), setDismissedPath(location.pathname)) : (setDismissedPath(null), chat.open()))}
                      aria-label="Assistant"
                      aria-pressed={showAside}
                      c="gray.1"
                    >
                      <IconMessageChatbot size={20} />
                    </ActionIcon>
                  </Indicator>
                </Tooltip>
              )}
              <ActionIcon variant="subtle" size="lg" component={Link} to="/notifications" aria-label="Notifications" c="gray.1">
                <Indicator disabled={!overview?.unreadNotifications} color="red" size={8}>
                  <IconBell size={20} />
                </Indicator>
              </ActionIcon>
              <Menu position="bottom-end" withArrow>
                <Menu.Target>
                  <UnstyledButton className="sg-account" aria-label="Account">
                    <IconUser size={16} />
                    <span className="sg-account-name">{user?.displayName} · {user ? roleLabel[user.role] : ''}</span>
                  </UnstyledButton>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item component={Link} to="/settings/tokens">Access tokens</Menu.Item>
                  <Menu.Item component={Link} to="/settings/account">Change password</Menu.Item>
                  <Menu.Divider />
                  <Menu.Item color="red" leftSection={<IconLogout size={16} />} onClick={logout}>Sign out</Menu.Item>
                </Menu.Dropdown>
              </Menu>
            </Group>
          </Group>
          {gardens.length > 0 && (
            <Select
              aria-label="Garden"
              data={gardens.map((g) => ({ value: g.id, label: g.name }))}
              value={gardenId || null}
              onChange={(v) => v && select(v)}
              allowDeselect={false}
              searchable
              size="sm"
              hiddenFrom="sm"
              mx="xs"
              comboboxProps={{ width: 'target', position: 'bottom' }}
            />
          )}
        </div>
      </AppShell.Header>

      <AppShell.Navbar p="sm">
        <AppShell.Section grow component={ScrollArea}>
          {groups.map((group) => (
            <div key={group.label}>
              <div className="sg-nav-label">{group.label}</div>
              {group.items.map((n) => (
                <NavLink
                  key={n.to}
                  component={Link}
                  to={n.to}
                  label={n.label}
                  leftSection={<n.icon size={18} stroke={1.6} />}
                  rightSection={badge(n.to)}
                  active={n.to === '/' ? location.pathname === '/' : location.pathname.startsWith(n.to)}
                  onClick={close}
                />
              ))}
            </div>
          ))}
        </AppShell.Section>
        <AppShell.Section>
          <div className="sg-footer">On this network</div>
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>
        {children}
        {!desktop && companion && !onAssistantPage && (
          <section className="sg-walk-assistant" aria-label="Assistant">
            <div className="sg-walk-assistant-frame">
              <ChatDrawer embedded />
            </div>
          </section>
        )}
      </AppShell.Main>

      <AppShell.Aside p={0}>
        {showAside && <ChatDrawer onClose={() => { chat.close(); dismiss() }} />}
      </AppShell.Aside>

      <nav className="sg-tabbar" aria-label="Garden">
        <Link to="/" data-active={tabActive('/') && location.pathname === '/'} onClick={close}>Overview</Link>
        <Link to="/garden" data-active={location.pathname.startsWith('/garden') || location.pathname.startsWith('/beds') || location.pathname.startsWith('/plants')} onClick={close}>Beds</Link>
        <Link to="/assistant" data-active={onAssistantPage} onClick={close}>Assistant</Link>
        <button type="button" data-active={opened} onClick={toggle}>More</button>
      </nav>
    </AppShell>
  )
}
