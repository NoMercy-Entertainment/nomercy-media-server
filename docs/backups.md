# Backups and restore

The server backs up its databases and its settings, and can put a backup back.
A restore is all or nothing: it either replaces everything, or it changes
nothing.

## What a backup holds

A backup is one folder, called a **set**, under
`<data folder>/backups/sets/<id>/`. The id is the time it was made, in UTC,
for example `20261116-040000-1234567`. A set holds:

- `databases/app.db`, `media.db` and `queue.db`: the server settings, the
  library, and the job queue. They are copied with SQLite's online backup, so
  the server keeps running and the copies are consistent, rows that were still
  in the `-wal` file included. Each copy must pass SQLite's `quick_check`; if
  one does not, no ordinary set is made and the log says so loudly, so a
  damaged copy never pushes a good set out. The two sets made around a restore
  (below) are the exception: they keep a damaged copy, marked `damaged` with
  what the check found, because a damaged database is exactly when you
  restore. A set with a damaged database cannot itself be restored.
- `config/`: the whole settings folder.
- `manifest.json`: the server version, the last database migration of each
  database, and the size and SHA-256 of every file.

A set is written to a hidden `.incomplete-<id>` folder first and renamed into
place at the end, so a set you can see is always whole.

The data folder is `%LOCALAPPDATA%\NoMercy\data` on Windows and
`~/.local/share/NoMercy/data` on Linux and macOS (the `NoMercy_dev` folder in
development mode).

### Backups hold secrets

A set holds `app.db` (the server's sign-in) and the settings folder (API keys,
signing keys). On Linux and macOS every backup folder is created readable by
the server's user only (`0700`) and every file in it `0600`. On Windows the
backups sit in the user's own `LOCALAPPDATA`, whose permissions already keep
other users out. Treat a copy of a set taken off the server like a password.

A restore does not tighten the live files: the restored databases, the config
folder and every file in it get the modes the live ones had before, so a
setup where another user or group reads the config (a Docker `PUID`/`PGID`
split) keeps working.

## When backups are made

- Every day at 04:00, by the "Daily Database Backup" job.
- When the owner asks for one (see the API below).
- When a restore is asked for, of the state at that moment: `before restoring <id>`.
- When a restore is applied, of the files it replaced: `before restore of <id>`
  (see below).

The newest **five** sets are kept; older ones are deleted when a new set is
made. These are never deleted, whatever their age:

- the set a waiting restore will restore, and its `before restoring` set;
- the `before restoring` and `before restore of` sets of the last restore.

That protection belongs to the *last* restore only: the next restore takes
it over, and the sets of the one before it become ordinary sets that age out
with the five-set limit. Copy them elsewhere if you want to keep them.

A set with a damaged database does not count toward the five. Damaged sets
keep their own newest five, so they never push a good set out; asking to
restore one is refused at once, before any safety backup is made.

If the record of the last restore cannot be read, it is renamed to
`last-result.json.bad`, the error is logged, and pruning goes on without the
protection it gave.

Separately, right before a server update changes a database, that one
database is copied to `<data folder>/backups/<name>.<time>.db`. They are a
last resort for a failed upgrade and are not listed or restored through the
API. Such a copy is kept until every good set the server keeps is newer than
it, so about five days with the daily backup, and never more than the newest
five per database. The daily backup wrote copies with this name too before it
became a set; a server that was updated across that change loses those the
first time a set is made.

### When the disk is nearly full

Before a set is written the server removes what the backups folder holds for
nothing: any `.incomplete-<id>` folder a failed backup left behind, and the
single-database copies described above that are older than every kept set.
It then compares the size of the databases with the free space on the disk
that holds the backups. When the set would not fit, nothing is written and
the log says how much is needed and how much is free. A copy that fills the
disk half way would take the running server's own databases down with it.

## Restoring a backup

A restore runs when the server starts, before anything opens a database, so no
open connection can get in the way. Asking for a restore therefore restarts the
server:

1. The server checks the set: the manifest, every checksum, and that no
   database is newer than this server.
2. It backs up the current state (`before restoring <id>`), and only then
   writes the restore request. A request always names that backup; a start
   that finds it missing refuses the restore.
3. It restarts. The new process waits for the old one to exit, and takes an
   exclusive lock on `<data folder>/server.lock`, which a running server holds
   for its whole life. If another server process still has the data folder,
   or the lock cannot be taken at all (a read-only folder, a network share
   without locking), nothing is touched, the log says which, and the request
   stays for the next start; withdraw it with the API if you no longer want
   it. The server itself starts either way.
4. It checks that the databases, the config folder and the restore's work
   folder (`<data folder>/.restore-work`, next to the databases) are on one
   volume, following every link and junction to where the folders really are,
   and that the config folder is not itself a link or a mount point: the swap
   below relies on renames, which are only safe within one volume. The
   backups folder may be anywhere, a link or a mount on another disk included:
   the set is only copied from it.
5. It copies the set next to the live files, flushing every file to disk,
   checks the copy again, checks each database for damage, and updates an
   older database to this server's version.
6. Only then does it swap: each live database (with its `-wal` and `-shm`
   files) and the config folder are renamed aside, and the copies renamed into
   their place, after a journal of the swap is written to disk.
7. If any rename fails, everything is renamed back. If the server stops in the
   middle (a crash, a power cut), the next start finds the journal and puts
   everything back.
8. Once everything is in place, the files that were renamed aside are saved as
   a set of their own, `before restore of <id>`. This is the exact state the
   old server left when it stopped, including anything written after step 2,
   such as playback progress or a scan. If the server stops before that set is
   written, or writing it fails (a full disk), the files stay in
   `<data folder>/.restore-work/previous/` and every start tries again; the
   result says the restore was applied and its old state not saved yet. A new
   restore waits until they are saved.

What you see afterwards:

| Result | Meaning |
| --- | --- |
| `applied` | The backup is in place. `before restore of <id>` holds what it replaced. |
| `refused` | A check failed before anything moved. Nothing changed. |
| `rolled_back` | The swap failed or was cut off; every file is back as it was. |
| `rollback_incomplete` | The swap failed and putting the files back failed too. The server will not start until they are back; each start tries again. The original files are in `<data folder>/.restore-work/previous/`. |

### Which backups can be restored

- **A set with a damaged database:** no (`GET` shows `damaged: true` on it).

- **From this server version or an older one:** yes. An older database is
  updated to the current version during the restore, before it goes live.
- **From a newer server version:** no. The restore is refused; update the
  server first.

### Undoing a restore

Restore `before restore of <id>`: it holds everything as it was the moment
the old server stopped. `before restoring <id>` is the same state a little
earlier, from when the restore was asked for.

### After a restore

- Everything written after the restored backup was made is gone from the live
  server, which is the point of a restore. It is still in the
  `before restore of <id>` set.
- The server's sign-in is part of the settings database. If the account token
  in the backup has expired, the server asks the owner to sign in again.
- Port settings are read before the restore is applied, so a backup with
  different ports takes effect at the next restart.

### What a power cut can still do

Every staged file and the journal are flushed to disk before the swap. What
.NET cannot do on every system is flush a *folder*, so a rename done just
before a power cut may be lost. The journal covers that: a lost rename is
undone, or simply not there, at the next start. A restore never ends half
applied, but one cut at the wrong moment can end rolled back.

### Docker and other mounts

A restore renames the config folder as a whole, so a config folder that is
its own bind mount (`-v …:/…/config`) is refused with that reason; mount the
folder above it instead. A bind mount of the whole app folder, or of the data
folder together with config on one volume, works. The backups folder can be
mounted anywhere.

## API

All endpoints need the server **owner**; anyone else gets `403`, and a caller
who is not signed in `401`.

| Method | Route | What it does |
| --- | --- | --- |
| `GET` | `/api/v1/dashboard/backups` | The sets (newest first), a waiting restore, and the result of the last restore. |
| `POST` | `/api/v1/dashboard/backups` | Makes a backup now and returns it. |
| `POST` | `/api/v1/dashboard/backups/{id}/restore` | Checks the set, backs up the current state, and restarts the server to restore it. Answers `202`. |
| `DELETE` | `/api/v1/dashboard/backups/pending-restore` | Withdraws a restore that is still waiting. `204`, or `404` when none is. |

The restore answers `400` for an id that is not a backup name, `404` for a set
that does not exist, `409` when another restore is already waiting, `422` with
the reasons when the set fails its checks, and `500` when the server cannot
restart (the restore is then withdrawn). After the restart, `GET` shows how it
ended under `last_restore`, with `pre_restore_backup_id` naming the
`before restore of` set.

## Restoring by hand

If the server cannot start at all, stop it and copy the files of a set back
yourself: delete any `app.db-wal`, `media.db-wal`, `queue.db-wal` and `-shm`
files in the data folder, copy the set's `databases/*.db` over the live ones,
and replace the `config` folder with the set's `config` folder. Keep the files
you replace until the server runs again.
