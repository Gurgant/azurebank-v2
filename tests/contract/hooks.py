"""
Schemathesis hooks for the AzureBank API, written for schemathesis 4.27.1 -- the version CI pins
(SCHEMATHESIS_VERSION in .github/workflows/ci.yml).

They make a LOCAL run test the operations instead of the front door:

- Every request carries X-AzureBank-Service-Key (ADR-0055). Without it the API answers
  401 SERVICE_CREDENTIAL_REQUIRED to everything; measured 2026-09-23, a run without it reported
  all 28 operations as "returned only 401/403 responses".
- Every operation the contract does not declare anonymous -- `security: [{}]`, which today is
  register, login and refresh -- carries a bearer token: a throwaway user's, registered here, or
  the one AZUREBANK_CONTRACT_TOKEN hands over.

The key is read from the environment, AZUREBANK_SERVICE_KEY: never a command-line argument, and
never this file. Run from the repository ROOT, where `tests.contract.hooks` resolves:

    export AZUREBANK_SERVICE_KEY="..."    # the value of the API's ServiceCredential:BffKey
    schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json

The configuration file loads this module through its `hooks` key. Without the file, the same
module loads from the environment -- 4.27.1 has no --hooks flag:

    SCHEMATHESIS_HOOKS=tests.contract.hooks schemathesis run docs/api/openapiv1.json --url http://localhost:5068

CI's conformance job loads this module too, through the configuration file (backlog row 41), so
a broken hook is a red job. It logs the seeded demo user in first and hands the token over in
AZUREBANK_CONTRACT_TOKEN. Until row 41 it loaded neither file and passed the token and the key as
-H arguments, so nothing in CI proved this file worked.

Until 2026-09-23 this was a v3-era file that 4.27.1 refused at import (backlog row 38).
"""

import os
import secrets
import uuid

import requests
import schemathesis

SERVICE_KEY_ENV = "AZUREBANK_SERVICE_KEY"
SERVICE_KEY_HEADER = "X-AzureBank-Service-Key"
# Optional: the bearer token of a user who already exists, used instead of registering one.
TOKEN_ENV = "AZUREBANK_CONTRACT_TOKEN"


def _service_key() -> str:
    key = os.environ.get(SERVICE_KEY_ENV, "")
    if not key:
        raise RuntimeError(
            f"{SERVICE_KEY_ENV} is not set. Export the value of the API's "
            "ServiceCredential:BffKey: without it every request answers 401 "
            "SERVICE_CREDENTIAL_REQUIRED (ADR-0055)."
        )
    return key


# Read once, at import: a missing key stops the run before its first request, not after it.
_SERVICE_KEY = _service_key()


@schemathesis.hook
def before_call(context, case, kwargs):
    """The service key on EVERY request -- the anonymous operations included."""
    case.headers[SERVICE_KEY_HEADER] = _SERVICE_KEY


def _is_anonymous(ctx) -> bool:
    return ctx.operation.definition.raw.get("security") == [{}]


# retry_on=[] turns off the library's reactive re-authentication. Its default treats any 401 as an
# expired token: it fetches a new one -- here, registers another user -- REPLAYS the request, and
# the checks judge the replay instead of the answer the request got. On this API a 401 can be
# exactly that answer: INVALID_PIN, AUTHORIZATION_REQUIRED, AUTHORIZATION_INVALID. Measured
# 2026-09-23, the same command registered one user with it off and five with the default. The
# token lives 15 minutes (Jwt:ExpirationMinutes) and the cache refetches it every 5
# (refresh_interval's default), so it never expires mid-run anyway.
@schemathesis.auth(retry_on=[]).skip_for(_is_anonymous)
class ThrowawayUser:
    """A new user per token fetch: no fixture to seed, and no state shared between runs -- unless
    AZUREBANK_CONTRACT_TOKEN hands over the token of a user who already exists."""

    def get(self, case, context):
        # CI's conformance job hands over the SEEDED demo user's token: the user it has always
        # run as, and the one that gives GET /api/transactions/{id} a 200 to check at all.
        # Measured 2026-09-24, every check, seed 42, 100 examples, one run each: 94 answers of
        # 200 (four transactions) with the demo user's token handed over this way, none as a
        # throwaway user. A handed-over token is never renewed, so it has to outlive the run:
        # CI's does, since the login is the step just before it and a run takes minutes.
        handed_over = os.environ.get(TOKEN_ENV)
        if handed_over:
            return handed_over
        schema = context.operation.schema
        config = schema.config
        tag = "st_" + uuid.uuid4().hex[:12]  # the contract's azureTag: ^[a-z][a-z0-9_]{2,19}$
        response = requests.post(
            schema.get_base_url().rstrip("/") + "/api/auth/register",
            json={
                "azureTag": tag,
                "email": f"schemathesis.{tag}@example.com",
                # Random, and never used again: nothing logs this user in.
                "password": secrets.token_urlsafe(18) + "aA1!",
                "firstName": "Schemathesis",
                "lastName": "Contract",
            },
            headers={SERVICE_KEY_HEADER: _SERVICE_KEY},
            timeout=config.request_timeout_for(operation=context.operation),
            verify=config.tls_verify_for(operation=context.operation),
        )
        if response.status_code != 201:
            raise RuntimeError(
                f"Registering the throwaway user answered {response.status_code}: {response.text}"
            )
        return response.json()["data"]["token"]["accessToken"]

    def set(self, case, data, context):
        case.headers["Authorization"] = f"Bearer {data}"
