# The documentation site

The tools that turn `docs/`, `README.md` and `SECURITY.md` into the site published at
<https://gurgant.github.io/azurebank-v2/docs/>. The pages themselves are not here: they stay where
they are, and nothing in `docs/` is written for this site.

- `npm ci --ignore-scripts` installs the packages that `package-lock.json` names.
- `npm run dev` copies the pages into `src/` and serves the site at
  `http://localhost:5173/azurebank-v2/docs/`. After an edit in `docs/`, run `npm run stage` again.
- `npm run build` builds the site into `.vitepress/dist`, and fails on a broken link: a file that
  does not exist, a page the site does not have, a `#fragment` that no heading gives.

Inline code is shown as written. Outside code, a page cannot hold two opening braces in a row: the
site would read them as an expression, so the build stops and names the line.

A page cannot make the site paste another file into it: an `@include` comment anywhere in a page,
or a line that starts with `<<<` outside a fenced code block, stops the build with the page and the
line. GitHub shows neither, so the site would say more than the page does.

A link from a page to a file that is not a page, such as source code, becomes a link to that file
on GitHub at `main`.
