import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useContext, type ReactNode } from 'react'
import { api } from './api/client'
import type { Role, User } from './api/types'

interface AuthStatus {
  needsSetup: boolean
  /** False while PostgreSQL is still starting or unreachable. Missing means an older server that already answered. */
  databaseReady?: boolean
  error?: string | null
  user: User | null
  database?: { host: string; port: number; database: string; username: string; sslMode: string }
}

export type AuthPhase = 'loading' | 'waiting' | 'setup' | 'login' | 'app'

/**
 * Sign-in is only for a server that answered and already has a user.
 * A failed status call, or a database that is not ready, used to fall through to the login form
 * (`needsSetup ?? false`) even when the user table was empty.
 */
export function authPhase(loading: boolean, user: User | null, status: AuthStatus | undefined, failed: boolean): AuthPhase {
  if (user) return 'app'
  if (loading && !status && !failed) return 'loading'
  if (failed || !status || status.databaseReady === false) return 'waiting'
  if (status.needsSetup) return 'setup'
  return 'login'
}

interface AuthContextValue {
  status: AuthStatus | undefined
  phase: AuthPhase
  statusError: string | null
  loading: boolean
  user: User | null
  canWrite: boolean
  isAdmin: boolean
  refresh: () => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue>(null!)

export const roleLabel: Record<Role, string> = { admin: 'Admin', read_write: 'ReadWrite', read_only: 'ReadOnly' }

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['auth'],
    queryFn: () => api.get<AuthStatus>('/api/auth/status'),
    staleTime: 60_000,
    retry: 0,
    refetchIntervalInBackground: true,
    refetchInterval: (query) => {
      const current = query.state.data
      if (query.state.status === 'error' || !current || current.databaseReady === false) return 2_000
      return false
    },
  })
  const user = data?.user ?? null
  const failed = isError
  const phase = authPhase(isLoading, user, data, failed)
  const statusError = isError
    ? (error instanceof Error ? error.message : 'The server did not answer.')
    : data?.databaseReady === false
      ? (data.error || 'The database is still starting.')
      : null
  const value: AuthContextValue = {
    status: data,
    phase,
    statusError,
    loading: isLoading,
    user,
    canWrite: user?.role === 'admin' || user?.role === 'read_write',
    isAdmin: user?.role === 'admin',
    refresh: async () => {
      await refetch()
    },
    logout: async () => {
      await api.post('/api/auth/logout')
      qc.clear()
      await refetch()
    },
  }
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export const useAuth = () => useContext(AuthContext)
