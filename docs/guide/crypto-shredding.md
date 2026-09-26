# Crypto-shredding: erasure that reaches every copy

**In short.** In the default plaintext mode, deleting a record removes it from the live tables only; every backup,
replica and WAL segment still holds its text. **Crypto-shredding** fixes that by never storing the text in the clear:
each record gets its own random key, held in a key store *outside* the database, and every free-text column erasure
removes is encrypted (AES-256-GCM) under it. Deleting the record destroys the key, so every copy of the ciphertext,
anywhere, becomes unreadable. It is opt-in. Two things stay readable in old copies because PostgreSQL has to search
them in the clear: the full-text search data and the embedding. And the guarantee is only as good as your key
store's custody.

Packages: `AgentExperience.Abstractions` (`IExperienceKeyStore`), `AgentExperience.Core` (`EnvelopeExperienceKeyStore`),
`AgentExperience.Storage.Postgres` (`ExperienceEncryption`), and `AgentExperience.Storage.Postgres.Vectors`. This is
the KL-2 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries).

## Turning it on

Give every PostgreSQL component one `ExperienceEncryption` over an `IExperienceKeyStore` whose keys live outside the
database's backups:

```csharp
using AgentExperience.Core.KeyManagement;
using AgentExperience.Storage.Postgres.DependencyInjection;

// Production: your KMS-backed IExperienceKeyEncryptionKey and your own repository. The two classes below are the
// reference implementations, for tests and local development only: an in-process KEK, and keys in memory -- a
// restart loses every key, which crypto-shreds the whole store.
var keyStore = new EnvelopeExperienceKeyStore(
    LocalExperienceKeyEncryptionKey.Generate("kek-2026-09"),
    new InMemoryExperienceWrappedKeyRepository());

services.AddAgentExperiencePostgresEncryption(keyStore);   // or new ExperienceEncryption(keyStore) per component
```

Pass the same instance to `PostgresExperienceEmbeddingIndex` when you use the vector channel. Plaintext mode keeps
working exactly as before, and an existing deployment switches with the [upgrade](#upgrading-a-plaintext-deployment)
below.

## What each mode guarantees

| After `DeleteAsync` returns `Deleted` | Plaintext mode (the default) | Encrypted mode |
| --- | --- | --- |
| Live rows in this database | Erased: the tombstone and the list in [Deletion and retention](deletion-and-retention.md#what-is-retained-after-a-delete-exhaustively) | Erased, the same way |
| The dead heap tuple, before `VACUUM` | **Readable**: it still holds the text | Holds only ciphertext nobody can open |
| Backups, replicas, WAL, `pg_dump`, replication streams | **Readable** in every copy made before the erasure | Hold only ciphertext nobody can open |
| The derived search data: `search_vector_sealed` (the task ID, summary and lesson as lexemes with positions — words as stems, an identifier-like task ID whole) and the vectors package's embedding and its content hash | Deleted from live rows; readable in every copy | **The same as plaintext mode**: deleted from live rows, readable in every copy. PostgreSQL has to read these in the clear to search, so they are never sealed |
| Identifiers and metadata (IDs — record, run, round, event, evidence, grant, feedback and assessment IDs — scope, statuses, scores and counters, timestamps, principal, reviewer, evaluator and administrator identities, measure kinds and values, trial labels, disclosure levels) | Deleted from live rows (the tombstone keeps its IDs and scope); readable in every copy | The same as plaintext mode: never sealed |
| Rows written before the deployment switched to encrypted mode | — | Record payloads: sealed by the upgrade job, but every copy made *before* it ran is plaintext. Append-only ledger rows (lifecycle reasons, evidence detail, feedback rationale, grant events) and grant reasons written before the switch: **stay plaintext**, in live rows until erased and in every copy |
| Exported telemetry, the server's own logs, external artifacts a record named | Out of reach | Out of reach. The text is sent to the server as statement parameters (the full-text vector is computed there), so a server that logs parameters (`log_statement`, `log_min_duration_statement`, `auto_explain`) writes them to its own log |
| A key store whose keys live in, or are backed up with, this database | — | **Nothing is guaranteed.** A restore brings back the key with the ciphertext |

The two claims the tests check with a real `pg_dump` and a raw `pageinspect` read of the dead tuple: in encrypted
mode neither holds the text, and after the erasure neither can be opened with any key the key store still holds; in
plaintext mode both still hold the text after the erasure.

## What is sealed

| Column | Sealed under | Stored in the clear instead |
| --- | --- | --- |
| `experience_records.payload` **and** `task_id` — task summary, attempts and tool calls, outcome and evidence detail, reflection and lesson, environment, provenance | the record's key, together as one value | `payload = {"sealed": "aexp-sealed:v1:…"}`, `payload_version = 2`, `task_id = '(sealed)'`, and the derived `search_vector_sealed` |
| `lifecycle_events.reason`, `lifecycle_events.confidence_detail` | the record's key | — |
| `confidence_evidence.detail` | the record's key | — |
| `experience_grants.reason`, `experience_grants.revocation_reason`, `experience_grant_events.reason` | the key of the record the grant is over | — |
| `experience_grants.approach_arguments` (`0017`) | not sealed: it holds the owner's tool names and argument keys, never a value, and is host configuration rather than captured content | the whole value |
| a reuse-feedback rationale | the key of **each** exposed record live in the submission's own scope, once per exposure, in `reuse_feedback_exposures.rationale_sealed` | `reuse_feedback.rationale = '(sealed)'` |

The feedback rule reproduces plaintext mode's erasure exactly: a submission survives while it names any record that
survives, and so does a readable copy of its rationale; once every record it was about is erased, no copy opens. A
rationale that names no live record in the submission's own scope — including one about a record read through a
sharing grant, which lives in its owner's scope — has no key to be sealed under and is not retained, although the
submission is recorded. A replay is compared against the rationale opened from any surviving copy; when none opens,
only its presence is compared.

**The format.** `aexp-sealed:v1:` followed by base64 of a 96-bit random nonce, the ciphertext and a 128-bit tag.
The associated data binds each value to its column, its record ID, its row (event, evidence, grant, grant event or
feedback ID), and all six scope fields, so a value moved to another record, row, column or scope fails its tag
rather than being read there. A failed tag throws `ExperienceStoreException`; nothing decrypted is ever returned. A
data key encrypts only its own record's handful of values, far inside the 2³² random-nonce bound per key.

## Key custody: the property is only as true as this

Keys come from an `IExperienceKeyStore` (`AgentExperience.Abstractions`): `CreateKeyAsync` (get-or-create),
`GetKeyAsync`, and `DestroyKeyAsync`, each for one record — its ID and its own scope. **A destroyed reference is
destroyed for ever**: the store never gives it a key again, which is what stops a late writer from making a
half-erased record look live. A sealed row whose key the store has **never** held is a configuration failure (the
wrong or an empty key store) and throws; only a destroyed key reads as erased.

`AgentExperience.Core` ships `EnvelopeExperienceKeyStore`: each data key is stored only *wrapped* by your
key-encryption key (KEK), through two small ports you implement over your KMS and your storage. The library takes no
KMS dependency.

| Port | Implement it over | Obligation |
| --- | --- | --- |
| `IExperienceKeyEncryptionKey` | Azure Key Vault `wrapKey`/`unwrapKey`, AWS KMS `Encrypt`/`Decrypt` with an encryption context, HashiCorp Vault transit | Bind the wrap to the `ExperienceKeyReference` (associated data or encryption context); keep old KEK versions unwrappable until re-wrapped |
| `IExperienceWrappedKeyRepository` | a table in a **different** database, a secrets store, a blob container | Atomic per record; keep a destroyed marker with no key material; **outside this database's backup domain** |

Rules for the production key store, each of which the property depends on:

- **Never in this database, on its replicas, or in its backups.** A key restored alongside its ciphertext protects
  nothing.
- **Its own backups bound the erasure.** A destroyed key survives in every key-store backup taken before the
  destruction. The retention of those backups is how long an erased record stays recoverable by someone holding both
  a database copy and a key-store copy — choose it as your erasure deadline.
- **Rotate the KEK and retire the old version to close that window early.** `RewrapAsync(batchSize)` moves every live
  data key under the KEK's current version, bounded and resumable (call it until `MoreRemain` is `false`), and
  never writes back a key destroyed meanwhile. Once it is done, retire the old KEK version in your KMS: every
  key-store backup wrapped under it becomes unusable too.
- **No caching.** The store asks the key store on every read and never caches a key; a production key store that
  caches extends its own erasure window by the cache's lifetime. The cost is one key-store call per sealed record
  read, made in turn while the reader holds its connection: a search returning twenty sealed records makes twenty.
- **It is on the erasure's critical path.** An unreachable key store makes every delete fail closed (below), and
  every read of a sealed record fail. So does a sealed row that will not open (a tampered value, or a key the store
  never held): the whole read, search or scan it is part of throws, loudly, rather than dropping the row. A failed
  rotation stops the same way: `RewrapAsync` throws on a key it cannot unwrap, and keeps throwing on it, until the
  KEK version that wrapped it is available again.

## The erasure's consistency rule

"A sealed record is erased once its key is destroyed." In encrypted mode `DeleteAsync`, and the sweep, which erases
through the same path, run in **one transaction**: the unchanged `purge_experience_record` first — scope and revision
guards, `EXECUTE` on the function, the tombstone, all uncommitted — then `DestroyKeyAsync`, then the commit. A refusal
of any kind (another scope, a stale revision, no `EXECUTE`, a failing statement) destroys no key. Once the key store
has been asked to destroy, the caller's cancellation token no longer applies: the destruction and the commit run to
the end, so a cancelled request cannot leave the key gone and the row live.

| What fails | What you see | State afterwards |
| --- | --- | --- |
| The key store, destroying the key | `ExperienceStoreException` (a sweep: `ExperienceRetentionSweepInterruptedException` with its partial count) | The transaction rolled back: the record is live, readable, and its key alive. **It never looks erased while its key survives** |
| The commit, after the key is destroyed (a lost connection, a crash) | `ExperienceStoreException` | The row is still there, but every read treats a sealed row whose key is destroyed as a tombstone (`Deleted` to the owner, `NotFound` through a grant, absent from query, text and vector search, history, grants and the re-index scan), and every write is refused (`Deleted`, `Conflict`, `NotFound`, `Missing` or `Invalid`, as for a tombstone), because the key store will not create its key again. **A sealed record never looks live while its key is gone.** Its derived search data, its grants and its ledger rows are still in the live tables until the tombstone is written: **retry the delete** that threw (it is idempotent) — the next sweep past its age, or the upgrade job, also finishes it. A plaintext row written before the upgrade is not protected by its key; it reads as live until the retry erases it |
| A process with no `ExperienceEncryption` deletes a sealed record | `ExperienceStoreException` (`42501`) | Nothing erased, key alive. `0016`'s guard refuses to tombstone a sealed row unless the transaction declares that it destroys the key (`SET LOCAL agent_experience.erasure_destroys_key = 'on'`, which the encrypted-mode store sets). Like `0010`'s marker it guards against a mistake, not an adversary: any session can set it |

A record that is already a tombstone has its key destroyed again, idempotently, on every repeat delete — so a
tombstone written around the library still loses its key. Key destruction runs while the transaction holds the row,
so it costs a row lock for the length of one key-store call.

**Other things to know.** Text in the sealed format (`aexp-sealed:v1:…`) is refused on write in both modes, as is a
feedback rationale that is exactly `(sealed)`: a stored value with that prefix is opened as ciphertext. The reverse is
not detected: a writer that can bypass the library can store plaintext in a sealed column, and it reads back as
stored, because a row written before the upgrade looks the same. A search whose `LIMIT` reaches records in the
half-erased state above returns fewer than it could. A key is created for a write before the database confirms the
record, so a write that then fails (a foreign or unknown ID) leaves an unused key in the key store; it encrypts
nothing. And a host that turns on Npgsql's `Include Error Detail` gets row values, including sealed search lexemes
and every plaintext row value, in constraint-violation messages: leave it off.

## Search, and why the residual is what it is

Sealing the search data would mean giving up full-text and vector retrieval in encrypted mode, which is the product.
So encrypted mode keeps them, and the residual above is exactly them:

- **Full text.** A sealed record's `search_vector_sealed` is computed, at write time, from exactly the expression
  `0003`'s generated `search_vector` uses — task ID, summary and lesson, bounded to 100000 characters — so a sealed
  record ranks exactly as its plaintext twin. The raw text is never stored; the tsvector's lexemes and positions are.
  The generated `search_vector` of a sealed row holds only the placeholder. Search matches `search_vector` for a
  plaintext row and `search_vector_sealed` for a sealed one, through two GIN indexes, and ranks on
  `coalesce(search_vector_sealed, search_vector)`. Erasure clears `search_vector_sealed` (a `0016` trigger on the
  tombstone transition).
- **Vectors.** The embedding is computed from the summary the re-index scan opens in process with the record's key;
  it and its SHA-256 content hash are stored as before. See [Indexing](indexing.md#what-is-embedded-and-what-is-stored).

## Upgrading a plaintext deployment

1. **Migrate** as the owner: `0016_crypto_shredding` adds two nullable columns, two partial indexes, three
   `NOT VALID` checks, one trigger and the sealing function. It touches no row. On a large table build the two
   indexes out of band first; the script's header has the `CONCURRENTLY` statements.
2. **Stand up the key store** outside the database's backup domain (above).
3. **Configure every component with the same `ExperienceEncryption`** and deploy. From here on every new record and
   every new ledger row is sealed. A process left in plaintext mode keeps writing plaintext, and fails loudly on the
   first sealed record it reads.
4. **Grant the job its function**: re-run `ApplyApplicationRolePrivilegesAsync` with the **same options as today
   plus** `AllowSealing = true` — the call is declarative, so leaving out `AllowErasure` would take erasure away.
   `AllowSealing` is a content-rewrite power: the function checks the shape of what it stores, never its meaning
   (it holds no key), so a role with it can replace any live plaintext record's payload with a well-formed seal of
   anything. Grant it for the upgrade only, or run the job under a separate, short-lived role.
5. **Seal the existing records**, oldest first, in bounded, resumable, authorized batches, until `MoreRemain` is
   `false` for every project:

   ```csharp
   ExperienceSealingResult batch;
   do
   {
       batch = await store.SealPlaintextRecordsAsync(auth, projectScope, batchSize: 200, ScopeMatch.Subtree, ct);
   }
   while (batch.MoreRemain);
   ```

   Each record is sealed in its own transaction: locked, keyed, sealed, the seal opened again under the record's own
   associated data, compared with the stored text and decoded as a payload, and only then written by
   `seal_experience_record`, which admits one transition — a live plaintext row at the revision read into its sealed
   shape — and copies the row's own full-text vector. Nothing about the record changes: its revision, status,
   ranking, embedding and history read exactly as before. `auth` must permit the root scope, as for a sweep; without
   `AllowSealing` the call fails with a permission error and seals nothing. A record whose key is already destroyed (a
   delete that did not commit) is erased instead, finishing that delete, which needs `AllowErasure`. A record that
   cannot be sealed stops the batch with an exception; the records sealed before it stay sealed, and a re-run starts
   from it. It is the `record.seal` telemetry operation.
6. **Take `AllowSealing` away again** on the next deploy: re-apply the same options without it.
7. **Deal with the copies the job cannot reach**: every backup, replica, WAL archive and dump taken before step 5
   still holds the plaintext, and so does each sealed record's dead tuple until `VACUUM`. Run `VACUUM` on
   `agent_experience.experience_records`, and age out the pre-upgrade backups on your normal schedule. Ledger rows
   and grant reasons written before step 3 stay plaintext until their record is erased; the library does not open
   an `UPDATE` path on its append-only audit trail to re-encrypt them.
