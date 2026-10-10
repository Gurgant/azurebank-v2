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

// The comment that makes the site paste another file into the page. The site expands it
// wherever it stands, inside a code block too, and GitHub shows nothing of it.
const includeComment = /<!--\s*@include:/
// The line that makes the site import a file as a code block, after any list or quote marks.
const snippetImport = /^\s*(?:(?:>|[-*+]|\d+[.)])\s*)*<<</
// An inline code span: a run of backticks up to the next run of the same length.
const codeSpan = /(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g

// The things the site cannot take, said here with the file and the line.
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
  // A paragraph is a run of lines with no blank line and no fenced block between them. A code
  // span can wrap from one line to the next inside it, so braces are looked for in the whole
  // paragraph, with each code span blanked out and the line ends kept for the line number.
  let paragraph = []
  let firstLine = 0
  const endParagraph = () => {
    const text = paragraph.join('\n').replace(codeSpan, (span) => span.replace(/[^\n]/g, ' '))
    for (let at = text.indexOf('{{'); at !== -1; at = text.indexOf('{{', at + 2)) {
      const line = firstLine + text.slice(0, at).split('\n').length - 1
      problems.push(`${name}:${line}: "{{" outside code is read as a Vue expression`)
    }
    paragraph = []
  }
  let fence = ''
  readFileSync(file, 'utf8')
    .split(/\r?\n/)
    .forEach((line, index) => {
      if (includeComment.test(line)) {
        problems.push(`${name}:${index + 1}: an "@include" comment pastes another file into the page`)
      }
      const mark = /^\s*(`{3,}|~{3,})/.exec(line)
      if (mark) {
        endParagraph()
        if (!fence) fence = mark[1]
        else if (mark[1][0] === fence[0] && mark[1].length >= fence.length) fence = ''
      } else if (!fence) {
        if (snippetImport.test(line)) {
          problems.push(`${name}:${index + 1}: a line that starts with "<<<" imports a file as code`)
        }
        if (line.trim() === '') endParagraph()
        else {
          if (paragraph.length === 0) firstLine = index + 1
          paragraph.push(line)
        }
      }
    })
  endParagraph()
}

if (problems.length > 0) {
  console.error(`stage: ${problems.length} problem(s)\n  ${problems.join('\n  ')}`)
  process.exit(1)
}
console.log(`stage: ${files.length} pages in src/`)
