/**
 * The social card: GitHub's repository preview (1280x640) and a LinkedIn link card (1200x627), from
 * one template. `app.capture.ts` passes the light dashboard it has just taken and public/logo.svg,
 * as data URLs, and photographs the page at each size. Sized in viewport units, so both come out
 * with the same composition; the text keeps 64px or more from every edge, which both platforms may
 * crop.
 *
 * Colours are the brand's (docs/brand-assets.md): the mark's #0c6ea1 and #39b8db.
 */

/*
  eslint-disable no-restricted-syntax --
  The hardcoded-colour rule is about the app, where colour belongs to the theme. This is not the
  app: it is a standalone page photographed on its own, with no FluentProvider and no tokens to
  resolve, and its colours are the logo's own two hexes and shades of them — spelled out here as
  src/theme/ spells out the theme's, because this is where they are defined, not used.
*/

export function socialCard(hero: string, logo: string): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>AzureBank card</title>
    <style>
      :root {
        --ink: #f3f8fb;
        --muted: #b9d4e3;
        --accent: #39b8db;
        --deep: #06273b;
        --brand: #0c6ea1;
      }
      * { box-sizing: border-box; margin: 0; }
      html, body { width: 100vw; height: 100vh; overflow: hidden; }
      body {
        font-family: 'Segoe UI Variable Display', 'Segoe UI', system-ui, sans-serif;
        color: var(--ink);
        background:
          radial-gradient(120vh 90vh at 85% 20%, rgba(57, 184, 219, 0.22), transparent 60%),
          linear-gradient(135deg, var(--brand) 0%, var(--deep) 70%);
        display: grid;
        grid-template-columns: 44vw 1fr;
        align-items: center;
      }
      .text { padding: 0 0 0 5.2vw; display: flex; flex-direction: column; gap: 2.6vh; }
      .mark { display: flex; align-items: center; gap: 1.4vw; }
      .mark img { width: 5.4vw; height: 5.4vw; background: #fff; border-radius: 1.2vw; padding: 0.5vw; }
      h1 { font-size: 5.3vw; font-weight: 700; letter-spacing: -0.03em; line-height: 1; }
      .lede { font-size: 2.05vw; line-height: 1.3; max-width: 36vw; }
      .stack { font-size: 1.45vw; color: var(--muted); letter-spacing: 0.01em; }
      ul { list-style: none; padding: 0; display: flex; flex-direction: column; gap: 1.1vh; }
      li { font-size: 1.6vw; display: flex; align-items: center; gap: 0.8vw; }
      li::before {
        content: ''; width: 0.6vw; height: 0.6vw; border-radius: 50%;
        background: var(--accent); flex: none;
      }
      .where { font-size: 1.25vw; color: var(--muted); margin-top: 1vh; }
      .shot {
        justify-self: start;
        width: 62vw;
        border-radius: 1vw;
        box-shadow: 0 3vh 7vh rgba(0, 0, 0, 0.45), 0 0 0 1px rgba(255, 255, 255, 0.12);
      }
    </style>
  </head>
  <body>
    <div class="text">
      <div class="mark">
        <img src="${logo}" alt="" />
        <h1>AzureBank</h1>
      </div>
      <p class="lede">A personal banking app, built end to end as a portfolio project.</p>
      <p class="stack">.NET 10 · ASP.NET Core BFF · React 19 · SQL Server</p>
      <ul>
        <li>Every payment applied exactly once</li>
        <li>No token in the browser</li>
        <li>One PIN entry authorises one payment</li>
      </ul>
      <p class="where">github.com/Gurgant/azurebank-v2</p>
    </div>
    <img class="shot" src="${hero}" alt="" />
  </body>
</html>`;
}
