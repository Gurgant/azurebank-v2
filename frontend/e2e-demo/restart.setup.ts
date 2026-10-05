import { execFileSync } from 'node:child_process';
import { expect, test as setup } from '@playwright/test';
import { note } from './demoRun';

/**
 * Between the demo's two halves: the BFF and the API are restarted, so that every session is
 * gone and the copy a browser keeps is all a visitor has left.
 *
 * THIS STOPS PART OF THE STACK. No spec of the default run does: that run owns no part of the
 * backend (`playwright.config.ts`, above its `webServer`). It is why the demo has a run of its
 * own, by hand, on a stack nobody else is using (`playwright.demo.config.ts`).
 *
 * WHY A RESTART. The BFF keeps its sessions in memory (`backend/src/AzureBank.Bff/README.md`,
 * "Storage"), so a restarted BFF knows none of them. The project after this one starts from the
 * state the first one saved, signed in, and shows that the session is refused and that
 * "Continue with my copy" signs in all the same.
 *
 * THE CONTROL COMES FIRST: with the saved state, `GET /bff/auth/me` answers 200 before anything
 * is restarted. Without it the 401 afterwards could be a state that was never signed in, a
 * session that had already ended, or a cookie that was never sent.
 *
 * BOTH CONTAINERS, THE BFF FIRST. The API has no network of its own: it runs inside the BFF
 * container's (`network_mode: "service:bff"` in compose.yaml), so it is restarted once the BFF's
 * container is back. Each is found by the two labels compose puts on a container, its project
 * and its service, and restarted by its id with `docker restart`: no compose file is read, so
 * none of the variables compose.yaml asks for has to be in this shell. Exactly one running
 * container per service, or the step fails before anything is restarted. `docker` is run with
 * its arguments as a list, through no shell.
 *
 * THE WAIT IS FOR THE WORD `Healthy`, NOT FOR A 200. The BFF's `/health/ready` answers 200 while
 * the API is unreachable: its check then says `Degraded`, on purpose
 * (`backend/src/AzureBank.Bff/Health/BackendApiHealthCheck.cs`). A wait for the status would end
 * while the API is still starting.
 *
 * AND IT IS `toPass`, NOT `expect.poll`. Straight after a restart nothing listens on the port,
 * and the request does not answer: it throws. `expect.poll` ends on the first throw of the
 * function it polls; `toPass` takes a throw as "not yet" and asks again
 * (`e2e/auth.setup.ts` wraps its own probe in a `try` for the same reason).
 *
 * MEASURED on 2026-10-05 against compose.yaml with compose.demo.yaml (Production, Docker Desktop
 * on Windows, the project named with `-p`), one run: the control answered 200; each lookup found
 * one container; after the two restarts docker's own `StartedAt` of both had moved, the BFF's
 * 2.4 s before the API's; the wait asked three times and met a request that threw, then
 * `200 Degraded`, then `200 Healthy`; the step took 6.1 s. The two times below are ceilings
 * chosen well above that, not measurements. The refusals above (no session, no container, two
 * containers, no docker) were met against stand-ins for the port and for the `docker` command,
 * not on that stack.
 */

/** The compose project whose containers these are: compose.yaml's `name:`, unless `-p` was used. */
const PROJECT = process.env.E2E_DEMO_COMPOSE_PROJECT ?? 'azurebank';

/** What one `docker` command is given. A restart waits for the container to stop first. */
const DOCKER_MS = 60_000;

/** What the stack is given to be whole again once both containers are back. */
const WHOLE_AGAIN_MS = 120_000;

function docker(...args: string[]) {
  try {
    return execFileSync('docker', args, {
      encoding: 'utf8',
      timeout: DOCKER_MS,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (failure) {
    const said = failure instanceof Error ? failure.message : String(failure);
    throw new Error(
      `"docker ${args.join(' ')}" did not succeed. This step restarts two containers of the ` +
        `compose project "${PROJECT}" and needs the docker command and a running engine. ${said}`,
    );
  }
}

/** The one running container of `service`, by the labels compose gave it. */
function containerOf(service: 'bff' | 'api') {
  const labels = [`com.docker.compose.project=${PROJECT}`, `com.docker.compose.service=${service}`];
  const ids = docker('ps', '-q', '--filter', `label=${labels[0]}`, '--filter', `label=${labels[1]}`)
    .split(/\s+/)
    .filter(Boolean);
  expect(
    ids.length,
    `Exactly one running container must carry the labels ${labels.join(' and ')}, and ` +
      `${ids.length} do. Is the stack up, and is E2E_DEMO_COMPOSE_PROJECT its project's name?`,
  ).toBe(1);
  return { service, labels, id: ids[0] };
}

setup('the BFF and the API are restarted, and the stack is whole again', async ({ request }) => {
  setup.setTimeout(2 * DOCKER_MS + WHOLE_AGAIN_MS + 30_000);

  // CONTROL: the saved session is alive before the restart, so the 401 after it is the restart's.
  const before = await request.get('/bff/auth/me', { failOnStatusCode: false });
  await note('before the restart', { path: '/bff/auth/me', status: before.status() });
  expect(
    before.status(),
    'With the saved state, GET /bff/auth/me must answer 200 before anything is restarted. A 401 ' +
      'here means the state was saved signed out, the session has ended, or the cookie was not ' +
      'sent: it is Secure, and this request sends it over http to localhost, not to 127.0.0.1.',
  ).toBe(200);

  // Both found before either is touched: a stack with no API to bring back is not restarted.
  const bff = containerOf('bff');
  const api = containerOf('api');
  await note('the containers', { bff: bff.labels, api: api.labels });

  docker('restart', bff.id);
  docker('restart', api.id);

  // What each ask met, in order, so the note can say whether the port refused before it answered.
  const met: string[] = [];
  let body = '';
  await expect(async () => {
    let answer;
    try {
      answer = await request.get('/health/ready', { failOnStatusCode: false, timeout: 5_000 });
    } catch (refused) {
      met.push('threw');
      throw refused;
    }
    body = await answer.text();
    met.push(`${answer.status()} ${body.trim()}`);
    expect(answer.status()).toBe(200);
    expect(body.trim()).toBe('Healthy');
  }).toPass({ timeout: WHOLE_AGAIN_MS });

  // "threw x3", "200 Degraded x12", "200 Healthy x1": each run of the same answer, counted.
  const runs: string[] = [];
  for (const [index, one] of met.entries()) {
    if (index > 0 && met[index - 1] === one) continue;
    const next = met.findIndex((later, at) => at > index && later !== one);
    runs.push(`${one} x${(next === -1 ? met.length : next) - index}`);
  }
  await note('whole again', {
    path: '/health/ready',
    body: JSON.stringify(body),
    asked: met.length,
    threwBeforeTheFirstAnswer: met.findIndex((one) => one !== 'threw'),
    met: runs,
  });
});
