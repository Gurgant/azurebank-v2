// The documentation site: https://gurgant.github.io/azurebank-v2/docs/
//
// The pages are the Markdown files of the repository, unchanged: docs/**, README.md and
// SECURITY.md. stage.mjs copies them into src/ at the same paths. This file decides the address
// of each page, the navigation, and what a link becomes.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, posix, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import GithubSlugger, { slug as githubSlug } from 'github-slugger'
import { defineConfig } from 'vitepress'

const here = dirname(fileURLToPath(import.meta.url))
const repositoryRoot = resolve(here, '..', '..')
const srcDir = resolve(here, '..', 'src')
const REPOSITORY = 'https://github.com/Gurgant/azurebank-v2'
const BASE = '/azurebank-v2/docs/'
const SITE = `https://gurgant.github.io${BASE}`

if (!existsSync(join(srcDir, 'docs', 'README.md'))) {
  throw new Error('src/ is missing: run "npm run stage" first.')
}

const toPosix = (file: string): string => file.split('\\').join('/')

function markdownFiles(folder: string, found: string[] = []): string[] {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const full = join(folder, entry.name)
    if (entry.isDirectory()) markdownFiles(full, found)
    else if (entry.name.endsWith('.md')) found.push(toPosix(relative(srcDir, full)))
  }
  return found
}

// Every page, by its path in the repository: 'docs/adr/README.md', 'README.md'.
const pages = new Set(markdownFiles(srcDir))

// The file a page is written to: 'docs/adr/README.md' -> 'adr/index.md'.
function route(source: string): string {
  if (source === 'README.md') return 'project.md'
  if (source === 'SECURITY.md') return 'security.md'
  return source.replace(/^docs\//, '').replace(/(^|\/)README\.md$/, '$1index.md')
}

// The address of a page inside the site, without the base: 'docs/adr/README.md' -> '/adr/'.
function address(source: string): string {
  return '/' + route(source).replace(/\.md$/, '').replace(/(^|\/)index$/, '$1')
}

// VitePress names a page by its path in src/ in some places and by its route in others.
const sourceOf = new Map<string, string>()
for (const source of pages) {
  for (const name of [source, route(source)]) {
    if (sourceOf.has(name)) throw new Error(`Two pages want the name "${name}".`)
    sourceOf.set(name, source)
  }
}

// The ids GitHub gives the headings of a Markdown file that is not a page of the site.
function headingIds(file: string): Set<string> {
  const slugger = new GithubSlugger()
  const ids = new Set<string>()
  let fence = ''
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const mark = /^\s*(`{3,}|~{3,})/.exec(line)
    if (mark) {
      if (!fence) fence = mark[1]
      else if (mark[1][0] === fence[0] && mark[1].length >= fence.length) fence = ''
      continue
    }
    const heading = fence ? null : /^ {0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$/.exec(line)
    if (heading) {
      const text = heading[1].replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1').replace(/<[^>]+>|[`*~]/g, '')
      ids.add(slugger.slug(text))
    }
  }
  return ids
}

// What a link written in a page becomes.
//   another page of the site       -> its address in the site
//   a folder that has a README.md  -> that page
//   any other file or folder       -> the same path on GitHub, at main
//   a path that does not exist     -> an error, and the build stops
function resolveLink(href: string, from: string): string {
  if (href === '' || href.startsWith('#') || href.startsWith('//')) return href
  if (/^[a-z][a-z0-9+.-]*:/i.test(href)) return href
  if (href.startsWith('/')) throw new Error(`"${href}" starts with "/": write it relative to the page`)
  const hashAt = href.indexOf('#')
  const fragment = hashAt < 0 ? '' : href.slice(hashAt)
  const path = decodeURIComponent((hashAt < 0 ? href : href.slice(0, hashAt)).split('?')[0])
  const target = posix.normalize(posix.join(posix.dirname(from), path)).replace(/\/$/, '')
  if (target === '..' || target.startsWith('../')) throw new Error(`"${href}" leaves the repository`)
  if (pages.has(target)) return address(target) + fragment
  if (pages.has(`${target}/README.md`)) return address(`${target}/README.md`) + fragment
  const file = join(repositoryRoot, target)
  if (!existsSync(file)) throw new Error(`"${href}" points to ${target}, which does not exist`)
  if (statSync(file).isDirectory()) return `${REPOSITORY}/tree/main/${encodeURI(target)}`
  if (fragment && target.endsWith('.md')) {
    if (!headingIds(file).has(decodeURIComponent(fragment.slice(1)))) {
      throw new Error(`"${href}": no heading of ${target} gives ${fragment}`)
    }
  }
  return `${REPOSITORY}/blob/main/${encodeURI(target)}${fragment}`
}

// Raw HTML in a page: an image path is made relative with "./" so that the build takes the
// file in; a link has to be written in Markdown, where the rule above sees it.
function fixHtml(html: string, problems: string[]): string {
  return html.replace(/\b(href|src|srcset)="([^"]*)"/g, (whole, name: string, value: string) => {
    if (value === '' || /^(?:[a-z][a-z0-9+.-]*:|[#/.])/i.test(value)) return whole
    if (name === 'href') {
      problems.push(`a link to "${value}" in raw HTML: write it as a Markdown link`)
      return whole
    }
    if (value.includes(',')) {
      problems.push(`a srcset with more than one image: "${value}"`)
      return whole
    }
    return `${name}="./${value}"`
  })
}

function linkRule(md: any): void {
  md.core.ruler.push('repository_links', (state: any) => {
    const env = state.env ?? {}
    const names = [env.realPath, env.path]
      .filter((file: unknown): file is string => typeof file === 'string')
      .map((file: string) => toPosix(relative(srcDir, file)))
      .concat(typeof env.relativePath === 'string' ? [env.relativePath] : [])
    const from = names.map((name: string) => sourceOf.get(name)).find(Boolean)
    if (!from) return
    const problems: string[] = []
    const resolved = (href: string): string => {
      try {
        return resolveLink(href, from)
      } catch (error) {
        problems.push((error as Error).message)
        return href
      }
    }
    for (const block of state.tokens) {
      if (block.type === 'html_block') block.content = fixHtml(block.content, problems)
      for (const token of block.children ?? []) {
        if (token.type === 'link_open') token.attrSet('href', resolved(token.attrGet('href') ?? ''))
        if (token.type === 'html_inline') token.content = fixHtml(token.content, problems)
      }
    }
    if (problems.length > 0) throw new Error(`${from}\n  ${problems.join('\n  ')}`)
  })
}

// A fenced block marked `mermaid` becomes a diagram, drawn in the browser.
function mermaidBlocks(md: any): void {
  const fence = md.renderer.rules.fence
  md.renderer.rules.fence = (tokens: any[], index: number, ...rest: any[]): string => {
    const token = tokens[index]
    if (token.info.trim() !== 'mermaid') return fence(tokens, index, ...rest)
    return `<MermaidDiagram source="${encodeURIComponent(token.content)}" />\n`
  }
}

// Inline code is text: Vue does not read "{{ }}" inside it.
function literalInlineCode(md: any): void {
  const inline = md.renderer.rules.code_inline
  md.renderer.rules.code_inline = (tokens: any[], index: number, ...rest: any[]): string => {
    tokens[index].attrSet('v-pre', '')
    return inline(tokens, index, ...rest)
  }
}

function title(source: string): string {
  const heading = /^# +(.+?)\s*$/m.exec(readFileSync(join(srcDir, source), 'utf8'))
  if (!heading) throw new Error(`${source} has no first-level heading to use as its title.`)
  return heading[1].replace(/`/g, '').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

// The first paragraph of a page, as plain text, for search engines. A decision record opens
// with its status and date: those lines are passed over.
function firstParagraph(source: string): string {
  const blocks = readFileSync(join(srcDir, source), 'utf8').replace(/\r\n/g, '\n').split(/\n{2,}/)
  const block = blocks
    .slice(1)
    .find((candidate) => /^[A-Za-z*_[]/.test(candidate) && !/^\*\*(Status|Date|Decision)/.test(candidate))
  if (!block) return ''
  const text = block
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[`*_]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
  return text.length <= 160 ? text : `${text.slice(0, 157).replace(/\s+\S*$/, '')}…`
}

type Item = { text: string; link?: string; collapsed?: boolean; items?: Item[] }

// The navigation follows the table in docs/README.md, row by row: a row that names a page of
// docs/ becomes that page, and a row that names a folder, or a page inside one, becomes the
// folder with its pages. A page that no row leads to stops the build.
function sidebar(): Item[] {
  const listed = new Set(['README.md', 'SECURITY.md', 'docs/README.md'])
  const entries: Item[] = []
  const table = readFileSync(join(srcDir, 'docs', 'README.md'), 'utf8')
  for (const [, href] of table.matchAll(/^\|\s*\[[^\]]*\]\(([^)\s]+)\)/gm)) {
    const target = posix.normalize(posix.join('docs', href)).replace(/\/$/, '')
    const parts = target.split('/')
    if (parts.length === 2 && pages.has(target)) {
      listed.add(target)
      entries.push({ text: title(target), link: address(target) })
      continue
    }
    const folder = parts.slice(0, 2).join('/')
    const inside = [...pages].filter((page) => page.startsWith(`${folder}/`)).sort()
    if (inside.length === 0) throw new Error(`docs/README.md names "${href}", which holds no page.`)
    if (inside.every((page) => listed.has(page))) continue
    for (const page of inside) listed.add(page)
    const index = `${folder}/README.md`
    // The template of a decision record is a page, reached from the index of decisions. It is
    // no decision, so the menu does not list it.
    const others = inside.filter((page) => page !== index && !/\/0000-template\.md$/.test(page))
    const entry: Item = { text: title(index), link: address(index) }
    if (others.length > 0) {
      entry.collapsed = others.length > 8
      entry.items = others.map((page) => ({ text: title(page), link: address(page) }))
    }
    entries.push(entry)
  }
  const unlisted = [...pages].filter((page) => !listed.has(page))
  if (unlisted.length > 0) {
    throw new Error(`No row of the table in docs/README.md leads to: ${unlisted.join(', ')}`)
  }
  return [
    {
      text: 'Start here',
      items: [
        { text: 'Documentation', link: '/' },
        { text: 'The project', link: '/project' },
        { text: 'Security', link: '/security' },
      ],
    },
    ...entries,
  ]
}

export default defineConfig({
  lang: 'en',
  title: 'AzureBank docs',
  description:
    'How AzureBank works and why: its architecture, design decisions, security notes and runbooks. AzureBank is a banking demo built as a portfolio project.',
  base: BASE,
  srcDir: 'src',
  cleanUrls: true,
  rewrites: (id: string) => route(id),
  // A page may name http://localhost in its text; every other dead link stops the build.
  ignoreDeadLinks: 'localhostLinks',
  sitemap: { hostname: SITE },
  head: [
    // The entry page's icon, at the root of the same site.
    ['link', { rel: 'icon', href: '/azurebank-v2/favicon.svg', type: 'image/svg+xml' }],
    ['meta', { name: 'theme-color', content: '#0077b6' }],
  ],
  markdown: {
    // GitHub has no {.class} syntax: a brace in a page is a brace.
    attrs: false,
    // Heading ids as GitHub makes them, so that a #fragment works in both places.
    anchor: { slugify: (text: string) => githubSlug(text) },
    config(md) {
      linkRule(md)
      mermaidBlocks(md)
      literalInlineCode(md)
    },
  },
  transformPageData(pageData) {
    const source = sourceOf.get(pageData.filePath) ?? sourceOf.get(pageData.relativePath)
    if (!source) return
    const summary = firstParagraph(source)
    if (summary) pageData.description = summary
    pageData.frontmatter.head ??= []
    pageData.frontmatter.head.push([
      'link',
      { rel: 'canonical', href: SITE.replace(/\/$/, '') + address(source) },
    ])
  },
  themeConfig: {
    siteTitle: 'AzureBank docs',
    nav: [
      { text: 'The project', link: '/project' },
      { text: 'How it works', link: '/architecture/overview' },
      { text: 'Decisions', link: '/adr/', activeMatch: '^/adr/' },
      { text: 'Security', link: '/security' },
      { text: 'Try the demo', link: '/testing/try-the-demo' },
      { text: 'Live demo', link: 'https://gurgant.github.io/azurebank-v2/', target: '_self' },
    ],
    sidebar: sidebar(),
    outline: { level: [2, 3] },
    search: { provider: 'local' },
    socialLinks: [{ icon: 'github', link: REPOSITORY, ariaLabel: 'The repository on GitHub' }],
    editLink: { pattern: `${REPOSITORY}/blob/main/:path`, text: 'View this page on GitHub' },
    externalLinkIcon: true,
  },
})
