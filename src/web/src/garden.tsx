import { useQuery } from '@tanstack/react-query'
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api/client'

export interface GardenChoice {
  id: string
  name: string
}

interface GardenContextValue {
  gardens: GardenChoice[]
  gardenId: string
  select: (id: string) => void
}

const KEY = 'sg.gardenId'
const GardenContext = createContext<GardenContextValue>({ gardens: [], gardenId: '', select: () => {} })

export function GardenProvider({ children }: { children: ReactNode }) {
  const { data } = useQuery({ queryKey: ['gardens'], queryFn: () => api.op<GardenChoice[]>('list_gardens') })
  const [gardenId, setGardenId] = useState(() => localStorage.getItem(KEY) ?? '')

  useEffect(() => {
    if (!data?.length) return
    if (!data.some((g) => g.id === gardenId)) {
      setGardenId(data[0].id)
      localStorage.setItem(KEY, data[0].id)
    }
  }, [data, gardenId])

  const select = (id: string) => {
    setGardenId(id)
    localStorage.setItem(KEY, id)
  }

  return <GardenContext.Provider value={{ gardens: data ?? [], gardenId, select }}>{children}</GardenContext.Provider>
}

export const useGarden = () => useContext(GardenContext)
