# Runbook: encrypted personal data and its keys

Some columns hold nothing but ciphertext (issue 104): a traveller's passport number and expiry,
the number and expiry of any other travel document, and an agency's bank account number. They are
encrypted with AES-256-GCM under a key that lives in configuration, never in the database, and
every stored value records the id of the key that made it.

| Column | Purpose it is bound to |
|---|---|
| `orders.order_travellers.passport_number_encrypted` | `orders.order_travellers.passport_number` |
| `orders.order_travellers.passport_expiry_encrypted` | `orders.order_travellers.passport_expiry` |
| `supplier.passenger_documents.doc_number_encrypted` | `supplier.passenger_documents.doc_number` |
| `supplier.passenger_documents.expires_on_encrypted` | `supplier.passenger_documents.expires_on` |
| `payments.agency_bank_accounts.account_number_encrypted` | `payments.agency_bank_accounts.account_number` |

The purpose is authenticated with the value, so ciphertext moved from one column to another will
not decrypt in its new home. **Never rename one**: every value already stored was made under it.

## Three things to know before you touch this

1. **These columns cannot be searched, filtered, joined or indexed for equality.** The same
   passport number encrypts to different bytes every time. A `WHERE passport_number = …` compiles
   and then matches nothing, for ever, without an error. Find the row by what it belongs to — the
   order, the agency, the booking — and read the value from it. The bank account duplicate check in
   `BankAccountService` is the worked example: it loads the agency's accounts and compares them in
   memory.
2. **Losing every key loses the data.** There is no recovery: that is what encryption at rest
   means. Back the keys up wherever the platform's other secrets live, not in this repository.
3. **Nothing else ever sees the plaintext.** The audit log records `[redacted]` for an encrypted
   column (recognised by its value converter, not by its name), and the supplier call log redacts
   document numbers and expiries before a call is stored.

## Configuration

```
Security__FieldEncryption__ActiveKeyId=key2026_09
Security__FieldEncryption__Keys__key2026_09=<32 random bytes, base64>
```

A key id is 1–32 letters, digits or underscores — anything that would not survive an environment
variable name is refused. Generate a key with `openssl rand -base64 32`; `./scripts/setup.sh`
generates one into a developer's own `.env` when it creates it.

With **no** keys configured, the application starts and `migrate` runs, and the first read or write
of one of these columns fails with a message naming the settings. That is deliberate: a missing key
is an outage for those columns, never a reason to store a passport number in clear.

## Rotating a key

Values name their key, so a rotation is not a big-bang migration. Old keys stay configured for
decrypting until nothing is left under them.

1. Generate the new key and give it a dated id: `key2027_03`.
2. Add it **beside** the old one and point the active id at it:

   ```
   Security__FieldEncryption__ActiveKeyId=key2027_03
   Security__FieldEncryption__Keys__key2027_03=<new key>
   Security__FieldEncryption__Keys__key2026_09=<old key>      # still needed to read
   ```

3. Deploy. From this moment every value that is written is written under the new key; everything
   already stored still reads under the old one.
4. Re-encrypt what is left: run the Api's `migrate` command (`dotnet run --project
   services/TripsAgent.Api -- migrate`). It applies any pending migrations and then runs
   `FieldEncryptionBackfill`, which rewrites every value whose key id is not the active one. The log
   line says how many:

   ```
   Field encryption: 1423 stored value(s) encrypted or re-encrypted under the active key.
   ```

5. Run it again. When it reports `0`, nothing is left under the old key.
6. Remove the old key from configuration and deploy. Keep it in the secret store for a while: a
   restore of an older backup would need it.

**Do not remove an old key before step 5 reports zero.** A value whose key has gone fails to decrypt
with a message naming the key id, and the only fix is to put the key back.

## The first deployment of encryption

Encrypting a column that already holds data is a data migration, and SQL cannot do it — the key is
not in the database. `migrate` therefore does it in three steps, in one command:

1. Apply migrations up to and including `AddEncryptedPiiColumns`, which adds the ciphertext columns
   beside the plaintext ones.
2. Run `FieldEncryptionBackfill`, which encrypts what is stored — including passport numbers in the
   format they had before issue 104 (`AesGcmSecretProtector`, which needs
   `Security__SecretEncryptionKey` to still be configured).
3. Apply `DropPlaintextPiiColumns`, which **refuses** — changing nothing — while any row still holds
   a value the backfill has not encrypted.

So: always migrate with `dotnet run --project services/TripsAgent.Api -- migrate`. Running
`dotnet ef database update` by hand skips step 2 and stops at step 3 with
`Traveller documents or bank account numbers are still stored unencrypted`; the fix is to run the
migrate command.

## "Could not be decrypted"

`The value was encrypted under key 'X', which is not configured`
: The key was removed too early, or the environment is missing one. Put key `X` back.

`The computed authentication tag did not match`
: The stored bytes, the column, or the key is not what made the value. Causes, in the order to check
them: a value copied between columns by a hand-written `UPDATE`; a restore that mixed databases; a
key replaced under the same id (never do this — give a new key a new id).

Neither failure ever returns the wrong plaintext. That is why GCM was chosen: a booking that fails
loudly is recoverable, one that silently ticketed the wrong passport number is not.
