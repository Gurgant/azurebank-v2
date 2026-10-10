// After the build: every address, every image and every #fragment written in the built pages
// has to exist in the built site. One that does not stops the build.
import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const HOST = 'https://gurgant.github.io'
const BASE = '/azurebank-v2/docs/'
// Addresses on the same host that this build does not produce: the entry page and its icon.
const OUTSIDE = new Set(['/azurebank-v2/', '/azurebank-v2/favicon.svg'])
const here = dirname(fileURLToPath(import.meta.url))
const dist = join(here, '.vitepress', 'dist')

function walk(folder, found = []) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const full = join(folder, entry.name)
    if (entry.isDirectory()) walk(full, found)
    else found.push(relative(dist, full).split('\\').join('/'))
  }
  return found
}

const files = new Set(walk(dist))
const pages = [...files].filter((file) => file.endsWith('.html'))
const html = new Map(pages.map((page) => [page, readFileSync(join(dist, page), 'utf8')]))
const ids = new Map(
  pages.map((page) => [page, new Set([...html.get(page).matchAll(/\sid="([^"]*)"/g)].map((match) => match[1]))]),
)

// '/azurebank-v2/docs/adr/' -> 'adr/index.html'; '/azurebank-v2/docs/security' -> 'security.html'
function fileAt(pathname) {
  const path = decodeURIComponent(pathname).slice(BASE.length)
  const candidates =
    path === '' || path.endsWith('/') ? [`${path}index.html`] : [path, `${path}.html`, `${path}/index.html`]
  return candidates.find((candidate) => files.has(candidate))
}

function problemWith(reference, page) {
  const url = new URL(reference.replace(/&amp;/g, '&'), `${HOST}${BASE}${page}`)
  if (url.origin !== HOST) return ''
  if (!url.pathname.startsWith(BASE)) {
    return OUTSIDE.has(url.pathname) ? '' : 'an address on this host that the site does not have'
  }
  const file = fileAt(url.pathname)
  if (!file) return 'no such page or file'
  const id = decodeURIComponent(url.hash.slice(1))
  if (id && file.endsWith('.html') && !ids.get(file).has(id)) return `no element with the id "${id}"`
  return ''
}

// The check has to be able to fail: a page and a fragment that cannot exist must be reported.
if (!problemWith(`${BASE}no-such-page`, 'index.html') || !problemWith(`${BASE}#no-such-fragment`, 'index.html')) {
  console.error('check-links: a missing page and a missing fragment were not reported. Nothing was checked.')
  process.exit(2)
}

// Every page that stage.mjs copied has to be a page of the built site.
const staged = walk(join(here, 'src')).filter((file) => file.endsWith('.md')).length
const built = pages.filter((page) => page !== '404.html').length

const problems = []
let checked = 0
for (const page of pages) {
  for (const [, name, value] of html.get(page).matchAll(/\s(href|src|srcset)="([^"]*)"/g)) {
    const references = name === 'srcset' ? value.split(',').map((part) => part.trim().split(/\s+/)[0]) : [value]
    for (const reference of references) {
      if (reference === '' || /^(?:mailto:|tel:|data:)/i.test(reference)) continue
      checked += 1
      const problem = problemWith(reference, page)
      if (problem) problems.push(`${page}: ${reference} -> ${problem}`)
    }
  }
}

if (built !== staged || checked === 0) {
  console.error(
    `check-links: ${built} pages were built from ${staged}, with ${checked} references. The build is not whole.`,
  )
  process.exit(2)
}
if (problems.length > 0) {
  console.error(`check-links: ${problems.length} broken reference(s)\n  ${problems.join('\n  ')}`)
  process.exit(1)
}
console.log(`check-links: ${checked} references in ${built} pages, none broken`)
