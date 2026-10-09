# Runbook — a refresh token's reuse is recorded

**Symptom:** a `RefreshTokenReuse` row in the audit trail: a grant whose session had already ended
was presented again. Or code running inside the BFF's replica is suspected, and no row exists.

**Why this runbook exists:** the tripwire that writes that row revokes nothing
([ADR-0057](../adr/0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) §4.3, F3):
only code inside the replica can present a grant, and revoking one user's grants would not contain
that code. So a person reads the row and picks a lever. ADR-0057 §5 says what each lever does;
this page is how to pull it.

**Running the SQL.** Every statement here runs against the API's database. Pass `-I` to `sqlcmd`:
without it every UPDATE below fails with Msg 1934 (`QUOTED_IDENTIFIER`), because `RefreshTokens`
and `AspNetUsers` carry filtered indexes ([`engineering-traps.md`](../engineering-traps.md)).

---

## 1. Find the tripwire rows

The tripwire is pushed to nobody (ADR-0057 §7), so look for its rows:

```sql
SELECT e.Sequence, e.OccurredAt, e.ActorUserId, e.SubjectId
FROM AuditEvents e
WHERE e.Event = 'RefreshTokenReuse'
ORDER BY e.Sequence DESC;
```

`ActorUserId` is the user and `SubjectId` is the grant's row in `RefreshTokens`. A row written
while refresh tokens still rotated (ADR-0021, before ADR-0057) means something else: rotation's
reuse detection, which had already revoked every token of that user by itself.

## 2. Look at the grant

The cleanup sweep deletes expired rows, so it may already be gone:

```sql
SELECT t.Id, t.UserId, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.RevokedReason
FROM RefreshTokens t
WHERE t.Id = '<SubjectId from the row>';
```

## 3. Decide what the row means

The BFF sends no renewal after it has ended a session (ADR-0057 §6, anomaly 1), and nobody outside
the replica can present a grant (ADR-0057 §3). So a row is either innocent and costs nothing but
itself (a renewal the API stamped more than 30 s after the BFF sent it, ADR-0057 §7, or a bug),
or it is code inside the replica. The row cannot tell them apart. Pick a lever: step 4 for the
user, step 5 for everyone, step 6 when code inside the replica is suspected.

## 4. One user: revoke every live grant and raise the session stamp

Both statements run in one transaction, as when `POST /api/auth/logout` pulls the same lever
(ADR-0057 §5.3). Paste the user's id into both places:

```sql
SET XACT_ABORT ON;
BEGIN TRANSACTION;

UPDATE RefreshTokens
SET RevokedAt = SYSUTCDATETIME(), RevokedReason = N'ReuseContainment'
WHERE UserId = '<ActorUserId from the row>' AND RevokedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();

UPDATE AspNetUsers
SET SessionStamp = SessionStamp + 1, ConcurrencyStamp = CONVERT(nvarchar(36), NEWID())
WHERE Id = '<ActorUserId from the row>';

COMMIT TRANSACTION;
```

The first statement reports how many grants it revoked, 0 when none was live. The second must
report 1: a 0 means the id matched no user, and nobody was signed out. The new `ConcurrencyStamp`
is not optional: a request that loaded the user before the commit (a PIN change, say) writes the
whole row back through Identity, which checks only that column. Without the new value that write
puts the old stamp back; given it, the write fails and the stamp stays raised.

The BFF reads every signed-in user's stamp every 15 s and refuses each of the user's sessions at
its first request after that read: within about 20 s of the commit for up to 1,000 signed-in
users, and 5 s more for each further 1,000. The revoke is the fallback: while the BFF cannot read
the stamp, each session ends at its next renewal, after up to 7.5 minutes of use (ADR-0057 §5.3).

## 5. Everyone: restart the revision

Every session ends at its next request, because sessions live only in the BFF's memory
(ADR-0057 §5.2):

```bash
az containerapp revision restart -n <app> -g <resource-group> --revision <revision>
```

## 6. The nuclear lever: code inside the replica is suspected

No token scheme can see that code (ADR-0057 §7), so every secret it could hold is replaced.

1. Generate new values for `Jwt:Secret` and `ServiceCredential:BffKey`, and deploy both in **one**
   revision: a key rotation is a deployment event on both sides (ADR-0055 D4). The new revision
   is a restart: every session ends, and every access token already minted fails its signature
   check.
2. Once the new revision answers, take the database's time. It is the cutoff for step 3:

```sql
SELECT SYSUTCDATETIME() AS Cutoff;
```

3. Revoke every live grant created before the cutoff. None of them can be presented without the
   new key any more; this makes it certain:

```sql
UPDATE RefreshTokens
SET RevokedAt = SYSUTCDATETIME(), RevokedReason = N'Incident'
WHERE RevokedAt IS NULL AND ExpiresAt > SYSUTCDATETIME() AND CreatedAt < '<Cutoff from step 2>';
```

**Only grants created before the cutoff.** A grant created after it belongs to a session that
began after the restart. Revoked too, that session would be signed out at its next renewal, and
the API would log the Warning it keeps for an `Incident` grant: an innocent session would look
like the attacker's. A session that began between the new revision's first answer and the cutoff
is revoked anyway: the safe side to err on.

## Afterwards

A statement run by hand is not audited: nothing in the API performed it. Write down who ran it,
when, and why, somewhere the database cannot revise.
