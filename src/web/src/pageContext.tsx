import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'

export interface PageContextValue {
  route: string
  entityType?: string
  entityId?: string
  label?: string
}

interface Ctx {
  page: PageContextValue
  setPage: (p: Omit<PageContextValue, 'route'>) => void
}

const PageCtx = createContext<Ctx>(null!)

/** Tracks what the user is looking at so the assistant can default to that context. */
export function PageContextProvider({ children }: { children: ReactNode }) {
  const location = useLocation()
  const [details, setDetails] = useState<Omit<PageContextValue, 'route'>>({})
  useEffect(() => setDetails({}), [location.pathname])
  return <PageCtx.Provider value={{ page: { route: location.pathname, ...details }, setPage: setDetails }}>{children}</PageCtx.Provider>
}

export const usePageContext = () => useContext(PageCtx)

export function usePageEntity(entityType: string | undefined, entityId: string | undefined, label: string | undefined) {
  const { setPage } = usePageContext()
  useEffect(() => {
    if (label) setPage({ entityType, entityId, label })
  }, [entityType, entityId, label, setPage])
}
