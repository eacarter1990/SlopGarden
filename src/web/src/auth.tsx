import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useContext, type ReactNode } from 'react'
import { api } from './api/client'
import type { Role, User } from './api/types'

interface AuthStatus {
  needsSetup: boolean
  user: User | null
}

interface AuthContextValue {
  status: AuthStatus | undefined
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
  const { data, isLoading, refetch } = useQuery({ queryKey: ['auth'], queryFn: () => api.get<AuthStatus>('/api/auth/status'), staleTime: 60_000 })
  const user = data?.user ?? null
  const value: AuthContextValue = {
    status: data,
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
