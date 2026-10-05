import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Plugin, PluginOption } from 'vite';
import viteConfig from '../../../vite.config';
import { isDemoMode } from './demoMode';

/*
  The demo's tag in the dev server: the plugin of `vite.config.ts`, asked as vite asks it.

  No other file under `src` names `vite.config.ts` (`grep -rl 'vite\.config' src` finds this one
  alone), and `vitest.config.ts` is a config of its own. So this file is the one that fails when
  the plugin loses its `apply: 'serve'`, hands vite another tag, or starts answering a value of
  the variable that is not exactly `true`. It asks the plugin named `azurebank-demo-tag` and no
  other: a second plugin that added the same tag under another name, with no `apply`, would pass
  here, and the commands below would show it (tried: the build prints 1, the dev server has the
  tag twice).

  What is held here is what the plugin says of itself and what it hands vite. What vite then does
  with it is vite's and is not held here: that a build leaves a `serve` plugin out, where a tag
  "for the end of the head" is written, and that `vite preview` serves the built page as it is.
  Three commands look at those by hand, from `frontend/`:
    AZUREBANK_DEMO=true npm run build, then grep -c 'azurebank-demo' dist/index.html: prints 0
    AZUREBANK_DEMO=true npx vite --port 5199 --strictPort, then curl -s http://localhost:5199/:
      the tag once, on the line before `</head>`; started without the variable: none
    AZUREBANK_DEMO=true npx vite preview --port 5198 --strictPort over that build, then
      curl -s http://localhost:5198/: none

  The tag is typed out below and not taken from the product. The last test then asks the app's
  own `isDemoMode()` about a page that carries what the plugin handed over: renamed together in
  the plugin and here, the tag would still pass the tests before it, and not that one.
*/
const THE_TAG = {
  tag: 'meta',
  attrs: { name: 'azurebank-demo', content: 'true' },
  injectTo: 'head',
};

/** The config's plugins of the demo tag's name, however deep the config nests them: one is due. */
function demoTagPlugins(): Plugin[] {
  const flat = (options: PluginOption[]): Plugin[] =>
    options.flatMap((option) => {
      if (Array.isArray(option)) return flat(option);
      return option && 'name' in option ? [option] : [];
    });
  return flat(viteConfig.plugins ?? []).filter((plugin) => plugin.name === 'azurebank-demo-tag');
}

/**
 * The tags the plugin hands vite for a page, with `AZUREBANK_DEMO` set to `value` (`undefined`:
 * not set at all). "Nothing" is an empty list here, whichever way the plugin says it.
 */
function handedToVite(value: string | undefined): unknown {
  vi.stubEnv('AZUREBANK_DEMO', value);
  const hook = demoTagPlugins()[0]?.transformIndexHtml;
  const ask = typeof hook === 'object' ? hook.handler : hook;
  if (ask === undefined) return 'the plugin has no hook for the page';
  // The plugin reads neither the page nor the context vite passes, so it is asked with neither.
  const handed = (ask as () => unknown)();
  return handed === undefined || handed === null ? [] : handed;
}

describe("the demo's tag in the dev server", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('is one plugin, and for the dev server only', () => {
    const plugins = demoTagPlugins();

    expect({ plugins: plugins.length, apply: plugins[0]?.apply }).toEqual({
      plugins: 1,
      apply: 'serve',
    });
  });

  it('with AZUREBANK_DEMO=true it hands vite the one tag, for the end of the head', () => {
    expect(handedToVite('true')).toEqual([THE_TAG]);
  });

  it('with any other value, or with none, it hands vite nothing', () => {
    expect({
      notSet: handedToVite(undefined),
      empty: handedToVite(''),
      false: handedToVite('false'),
      one: handedToVite('1'),
      upperCase: handedToVite('TRUE'),
      aSpaceAfter: handedToVite('true '),
      aSpaceBefore: handedToVite(' true'),
      // The same question with the one value that counts, so the lists above are answers.
      andWithTrue: handedToVite('true'),
    }).toEqual({
      notSet: [],
      empty: [],
      false: [],
      one: [],
      upperCase: [],
      aSpaceAfter: [],
      aSpaceBefore: [],
      andWithTrue: [THE_TAG],
    });
  });

  it('a page that carries what it hands over is the demo for the app', () => {
    const handed = handedToVite('true');
    const added: Element[] = [];
    const before = isDemoMode();
    try {
      for (const { tag, attrs } of Array.isArray(handed) ? (handed as (typeof THE_TAG)[]) : []) {
        const element = document.createElement(tag);
        for (const [name, said] of Object.entries(attrs)) element.setAttribute(name, said);
        document.head.append(element);
        added.push(element);
      }

      expect({ before, tagsAdded: added.length, after: isDemoMode() }).toEqual({
        before: false,
        tagsAdded: 1,
        after: true,
      });
    } finally {
      for (const element of added) element.remove();
    }
  });
});
