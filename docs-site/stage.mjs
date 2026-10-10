// Copies the pages the site is built from into src/, each at its path in the repository:
// docs/**, README.md and SECURITY.md. Nothing in docs/ is changed by it.
//
// A folder of docs/ that has pages and no README.md gets a generated one in the copy, a list of
// its pages, so that a link to the folder has somewhere to land.
import { cpSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { basename, dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repository = resolve(here, '..')
const out = join(here, 'src')

rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
cpSync(join(repository, 'docs'), join(out, 'docs'), {
  recursive: true,
  filter: (source) => !basename(source).startsWith('.'),
})
for (const file of ['README.md', 'SECURITY.md']) cpSync(join(repository, file), join(out, file))

const firstHeading = (file) => /^# +(.+?)\s*$/m.exec(readFileSync(file, 'utf8'))?.[1]

for (const entry of readdirSync(join(out, 'docs'), { withFileTypes: true })) {
  if (!entry.isDirectory()) continue
  const folder = join(out, 'docs', entry.name)
  const pages = readdirSync(folder)
    .filter((name) => name.endsWith('.md'))
    .sort()
  if (pages.length === 0 || pages.includes('README.md')) continue
  const title = entry.name[0].toUpperCase() + entry.name.slice(1)
  const list = pages.map((name) => `- [${firstHeading(join(folder, name)) ?? name}](${name})`)
  writeFileSync(
    join(folder, 'README.md'),
    `---\neditLink: false\n---\n\n# ${title}\n\n${list.join('\n')}\n`,
  )
}

function markdownFiles(folder, found = []) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const full = join(folder, entry.name)
    if (entry.isDirectory()) markdownFiles(full, found)
    else if (entry.name.endsWith('.md')) found.push(full)
  }
  return found
}

// Three things the site cannot take, said here with the file and the line.
const problems = []
const files = markdownFiles(out)
for (const file of files) {
  const name = relative(out, file).split('\\').join('/')
  if (/(^|\/)index\.md$/.test(name)) {
    problems.push(`${name}: a page named index.md takes the address of the folder's README.md`)
  }
  if (name === 'docs/project.md' || name === 'docs/security.md') {
    problems.push(`${name}: the address is taken by README.md or SECURITY.md of the repository`)
  }
  let fence = ''
  readFileSync(file, 'utf8')
    .split(/\r?\n/)
    .forEach((line, index) => {
      const mark = /^\s*(`{3,}|~{3,})/.exec(line)
      if (mark) {
        if (!fence) fence = mark[1]
        else if (mark[1][0] === fence[0] && mark[1].length >= fence.length) fence = ''
      } else if (!fence && line.replace(/(`+).+?\1/g, '').includes('{{')) {
        problems.push(`${name}:${index + 1}: "{{" outside code is read as a Vue expression`)
      }
    })
}

if (problems.length > 0) {
  console.error(`stage: ${problems.length} problem(s)\n  ${problems.join('\n  ')}`)
  process.exit(1)
}
console.log(`stage: ${files.length} pages in src/`)
