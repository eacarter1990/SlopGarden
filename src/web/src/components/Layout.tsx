import { ActionIcon, AppShell, Badge, Burger, Group, Indicator, Menu, NavLink, ScrollArea, Text, Tooltip, useMantineColorScheme } from '@mantine/core'
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
  IconMoon,
  IconPlant2,
  IconSettings,
  IconShieldCheck,
  IconSun,
  IconChecklist,
} from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { api } from '../api/client'
import { useLive } from '../api/live'
import type { GardenOverview } from '../api/types'
import { roleLabel, useAuth } from '../auth'
import { ChatDrawer } from './ChatDrawer'
import { EmergencyStopButton } from './EmergencyStopButton'

const nav = [
  { to: '/', label: 'Dashboard', icon: IconLayoutDashboard },
  { to: '/garden', label: 'Garden', icon: IconPlant2 },
  { to: '/sensors', label: 'Sensors & readings', icon: IconGauge },
  { to: '/devices', label: 'Devices', icon: IconCpu },
  { to: '/automation', label: 'Automation', icon: IconCalendarTime },
  { to: '/safety', label: 'Safety', icon: IconShieldCheck },
  { to: '/assistant', label: 'Assistant', icon: IconMessageChatbot },
  { to: '/approvals', label: 'Approvals', icon: IconChecklist },
  { to: '/notifications', label: 'Notifications', icon: IconBell },
  { to: '/mcp', label: 'MCP tools', icon: IconApi },
  { to: '/settings', label: 'Settings', icon: IconSettings },
]

export function useOverview(gardenId?: string) {
  return useQuery({
    queryKey: ['overview', gardenId ?? 'default'],
    queryFn: () => api.op<GardenOverview>('get_garden_overview', gardenId ? { gardenId } : {}),
    refetchInterval: 30_000,
  })
}

export function Layout({ children }: { children: ReactNode }) {
  const [opened, { toggle, close }] = useDisclosure()
  const [chatOpen, chat] = useDisclosure(false)
  const location = useLocation()
  const { user, logout } = useAuth()
  const { connected } = useLive()
  const { colorScheme, toggleColorScheme } = useMantineColorScheme()
  const { data: overview } = useOverview()
  const wide = useMediaQuery('(min-width: 1400px)')
  const onAssistantPage = location.pathname.startsWith('/assistant')

  const badge = (to: string) => {
    if (to === '/approvals' && overview?.pendingProposals) return <Badge size="xs" color="violet" circle>{overview.pendingProposals}</Badge>
    if (to === '/devices' && overview?.devices.pending) return <Badge size="xs" color="orange" circle>{overview.devices.pending}</Badge>
    if (to === '/notifications' && overview?.unreadNotifications) return <Badge size="xs" color="red" variant="light">{overview.unreadNotifications > 99 ? '99+' : overview.unreadNotifications}</Badge>
    return null
  }

  return (
    <AppShell
      header={{ height: 60 }}
      navbar={{ width: 240, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      aside={{ width: wide ? 460 : 400, breakpoint: 'md', collapsed: { desktop: !chatOpen || onAssistantPage, mobile: !chatOpen || onAssistantPage } }}
      padding="lg"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <img src="/favicon.svg" width={30} height={30} alt="" />
            <Text fw={750} size="lg">SlopGarden</Text>
            {overview && <Text c="dimmed" size="sm" visibleFrom="md">· {overview.garden.name}</Text>}
          </Group>
          <Group gap="xs" wrap="nowrap">
            <Tooltip label={connected ? 'Live updates connected' : 'Reconnecting…'}>
              <span className="sg-dot" style={{ background: connected ? 'var(--mantine-color-green-6)' : 'var(--mantine-color-red-6)' }} />
            </Tooltip>
            <EmergencyStopButton scopeType="global" label="Stop all" compact />
            {!onAssistantPage && (
              <Tooltip label="Ask the assistant about this page">
                <Indicator disabled={!overview?.runningAiActivities.length} color="violet" processing size={9}>
                  <ActionIcon variant={chatOpen ? 'filled' : 'light'} color="violet" size="lg" onClick={chat.toggle} aria-label="Assistant">
                    <IconMessageChatbot size={20} />
                  </ActionIcon>
                </Indicator>
              </Tooltip>
            )}
            <ActionIcon variant="subtle" size="lg" component={Link} to="/notifications" aria-label="Notifications">
              <Indicator disabled={!overview?.unreadNotifications} color="red" size={8}>
                <IconBell size={20} />
              </Indicator>
            </ActionIcon>
            <ActionIcon variant="subtle" size="lg" onClick={toggleColorScheme} aria-label="Toggle color scheme">
              {colorScheme === 'dark' ? <IconSun size={20} /> : <IconMoon size={20} />}
            </ActionIcon>
            <Menu position="bottom-end" withArrow>
              <Menu.Target>
                <Badge variant="light" size="lg" style={{ cursor: 'pointer', textTransform: 'none' }}>
                  {user?.displayName} · {user ? roleLabel[user.role] : ''}
                </Badge>
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
      </AppShell.Header>

      <AppShell.Navbar p="sm">
        <AppShell.Section grow component={ScrollArea}>
          {nav.map((n) => (
            <NavLink
              key={n.to}
              component={Link}
              to={n.to}
              label={n.label}
              leftSection={<n.icon size={18} stroke={1.6} />}
              rightSection={badge(n.to)}
              active={n.to === '/' ? location.pathname === '/' : location.pathname.startsWith(n.to)}
              onClick={close}
              style={{ borderRadius: 8 }}
            />
          ))}
        </AppShell.Section>
        <AppShell.Section>
          <Text size="xs" c="dimmed" ta="center">Local network · v0.1 · Milestone 1</Text>
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>{children}</AppShell.Main>

      <AppShell.Aside p={0}>
        {chatOpen && !onAssistantPage && <ChatDrawer onClose={chat.close} />}
      </AppShell.Aside>
    </AppShell>
  )
}
