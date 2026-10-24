import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

export function Markdown({ children }: { children: string }) {
  return (
    <div className="sg-markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{ a: ({ href, children }) => <a href={href} target={href?.startsWith('/') ? undefined : '_blank'} rel="noreferrer">{children}</a> }}
      >
        {children}
      </ReactMarkdown>
    </div>
  )
}
