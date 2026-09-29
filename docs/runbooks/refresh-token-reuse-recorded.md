# Runbook — a refresh token's reuse is recorded

**Symptom:** a `RefreshTokenReuse` row in the audit trail: a grant whose session had already ended
was presented again. Or code running inside the BFF's replica is suspected, with no row at all.

**Why this runbook exists:** the tripwire that writes that row revokes nothing
([ADR-0057](../adr/0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) §4.3, F3).
Only code inside the replica can present a grant, and revoking one user's grants would not contain
that code, so nothing automatic acts on the row: a person reads it and picks a lever. ADR-0057 §5
says what each lever does and how fast it reaches a browser; this page is how to pull it.

**Running the SQL.** Every statement here runs against the API's database. With `sqlcmd`, pass
`-I`: without it every UPDATE below fails with Msg 1934 (`QUOTED_IDENTIFIER`), because
`RefreshTokens` and `AspNetUsers` carry filtered indexes (`IX_RefreshTokens_ReplacedByTokenId`;
`EmailIndex` and `UserNameIndex`). Measured 2026-09-28 against LocalDB, each UPDATE inside a
transaction that was rolled back; the reads ran without the flag
([`engineering-traps.md`](../engineering-traps.md) has why).

---

## 1. Find the tripwire rows

On Azure the event reaches the audit trail and nothing else, because application logs go nowhere
there (ADR-0057 §4.3):

```sql
SELECT e.Sequence, e.OccurredAt, e.ActorUserId, e.SubjectId
FROM AuditEvents e
WHERE e.Event = 'RefreshTokenReuse'
ORDER BY e.Sequence DESC;
```

`ActorUserId` is the user and `SubjectId` is the grant's row in `RefreshTokens`. **A row written
before PR-1 was deployed means something else:** rotation's reuse detection, which had already
revoked every token of that user by itself.

## 2. Look at the grant

The cleanup sweep deletes expired rows, so it may already be gone:

```sql
SELECT t.Id, t.UserId, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.RevokedReason
FROM RefreshTokens t
WHERE t.Id = '<SubjectId from the row>';
```

## 3. Decide what the row means

The legitimate BFF cannot send a renewal after it ended the session (ADR-0057 §6, anomaly 1), and
nobody outside the replica can present a grant (ADR-0057 §3). So a row is either something innocent
that costs this row and nothing else — the residual in ADR-0057 §7, a renewal the API stamped more
than 30 s after the BFF sent it, or a bug — or code inside the replica. The row cannot tell them apart. Pick
a lever: step 4 for the user, step 5 for everyone, step 6 when code inside the replica is
suspected.

## 4. One user: revoke every live grant and raise the session stamp

Both statements run in one transaction: the revoke and the raise of the user's `SessionStamp`
commit together or not at all, as they do when `POST /api/auth/logout` pulls the same lever
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

The new `ConcurrencyStamp` is not optional. A request that loaded the user before the commit — a
PIN change, say — writes the whole row back through Identity, stamp included, and Identity checks
only `ConcurrencyStamp`: without the new value that write puts the old stamp back and the lever
undoes itself. With it the write fails, and the stamp stays raised.

The BFF reads every signed-in user's stamp every 15 s, so each of the user's sessions is refused at
its first request after that read: within ~20 s of the commit with up to 1,000 signed-in users (one
read); each further 1,000 adds a read of up to 5 s. The revoke is the fallback: while the BFF cannot
read the stamp, each session still ends at its next successful renewal, after up to half its access
token's life (7.5 minutes) of continued use (ADR-0057 §5).

The first statement reports how many grants it revoked, 0 when none was live. The second must
report 1: a 0 there means the id matched no user, and nobody was signed out.

## 5. Everyone: restart the revision

Every session ends at its next request, because sessions live only in the BFF's memory
(ADR-0057 §5.2):

```bash
az containerapp revision restart -n <app> -g <resource-group> --revision <revision>
```

## 6. The nuclear lever: code inside the replica is suspected

No token scheme can see that code (ADR-0057 §7), so every secret it could hold is replaced.

1. Generate new values for `Jwt:Secret` and `ServiceCredential:BffKey`, and deploy both in **one**
   revision. The BFF and the API read them from the same app, and ADR-0055 D4 already makes a key
   rotation a deployment event on both sides. The new revision is a restart: every session ends,
   and every access token already minted fails its signature check.
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

**Only grants created before the cutoff.** A grant created after it belongs to a session that began
after the restart. Without the cutoff the statement would revoke those too: each such session would
be signed out again at its next renewal, and the API would log the Warning it keeps for an
`Incident` grant, so innocent sessions would look like the attacker's. A session that began between
the new revision's first answer and the cutoff is revoked anyway, and ends at its next renewal: the
safe side to err on. Measured 2026-09-28 against LocalDB, inside a transaction that was rolled
back, with the cutoff set to the 180th oldest `CreatedAt` among 360 live grants: 179 were revoked,
and the 181 created at or after the cutoff stayed live.

## Afterwards

A statement run by hand is not audited: nothing in the API performed it. Write down who ran it,
when, and why, somewhere the database cannot revise.
