import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
import '@mantine/charts/styles.css'
import '@fontsource-variable/source-sans-3'
import '@fontsource-variable/source-serif-4'
import './styles.css'
import { MantineProvider, createTheme, type CSSVariablesResolver, type MantineColorsTuple } from '@mantine/core'
import { ModalsProvider } from '@mantine/modals'
import { Notifications } from '@mantine/notifications'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import App from './App'
import { AuthProvider } from './auth'

const moss: MantineColorsTuple = ['#f2f6f3', '#e3ebe4', '#c5d5c8', '#9bb5a0', '#6d9178', '#4f7460', '#3d5c4a', '#314a3c', '#283d32', '#1c2e25']
const ember: MantineColorsTuple = ['#fdf3ef', '#f8e0d6', '#f0bba6', '#e48a6c', '#d65a38', '#d24a24', '#b83a18', '#962f14', '#7a2612', '#5c1c0e']
const ink: MantineColorsTuple = ['#f4f7f5', '#e6eeea', '#c5d2cb', '#8ea196', '#5e6a62', '#3c4a42', '#2a3830', '#1c2822', '#14211b', '#0c1410']

const theme = createTheme({
  primaryColor: 'green',
  primaryShade: 6,
  defaultRadius: 'sm',
  white: '#f7f1e6',
  black: '#14211b',
  fontFamily: '"Source Sans 3 Variable", "Source Sans 3", sans-serif',
  fontFamilyMonospace: '"Source Sans 3 Variable", ui-monospace, monospace',
  headings: {
    fontFamily: '"Source Serif 4 Variable", "Source Serif 4", Georgia, serif',
    fontWeight: '560',
  },
  colors: { moss, ember, ink, green: moss, red: ember },
  components: {
    Card: { defaultProps: { withBorder: true, radius: 12, padding: 'md', shadow: 'none' } },
    Paper: { defaultProps: { radius: 12, shadow: 'none' } },
    Button: { defaultProps: { radius: 'sm' } },
    Modal: { defaultProps: { radius: 12, overlayProps: { backgroundOpacity: 0.45, color: '#0c1410' } } },
  },
})

const cssVariablesResolver: CSSVariablesResolver = () => ({
  variables: {
    '--mantine-color-body': '#f3ecdf',
    '--mantine-color-text': '#14211b',
    '--mantine-color-dimmed': '#5e6a62',
    '--mantine-color-placeholder': '#7d877f',
    '--mantine-color-default': '#f7f1e6',
    '--mantine-color-default-hover': '#efe6d6',
    '--mantine-color-default-border': '#d9d0c0',
    '--mantine-color-anchor': '#14211b',
  },
  light: {
    '--mantine-color-body': '#f3ecdf',
    '--mantine-color-text': '#14211b',
    '--mantine-color-dimmed': '#5e6a62',
    '--mantine-color-default': '#f7f1e6',
    '--mantine-color-default-hover': '#efe6d6',
    '--mantine-color-default-border': '#d9d0c0',
    '--mantine-color-anchor': '#14211b',
  },
  dark: {},
})

const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false, retry: 1, staleTime: 5_000 } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver} defaultColorScheme="light" forceColorScheme="light">
      <Notifications position="top-right" />
      <QueryClientProvider client={queryClient}>
        <ModalsProvider>
          <BrowserRouter>
            <AuthProvider>
              <App />
            </AuthProvider>
          </BrowserRouter>
        </ModalsProvider>
      </QueryClientProvider>
    </MantineProvider>
  </StrictMode>,
)
