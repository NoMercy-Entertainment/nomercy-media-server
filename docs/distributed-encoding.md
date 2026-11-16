# Distributed Encoding

> Suspended: distributed/remote encoding workers are out of scope for this round, see audit/ALIGNMENT.md (D5). The protocol and this guide stay in the tree; there is no scheduled work to make it reliable.

NoMercy MediaServer supports splitting encode jobs across multiple machines —
one **coordinator** (your normal server) dispatching tasks to one or more
**remote workers** (extra boxes with spare CPU/GPU). When no workers are
registered the coordinator runs everything locally, so enabling distribution is
purely additive.

This guide walks through a two-machine setup: one coordinator + one remote
worker. Adding more workers is the same procedure repeated.

## What you need

- Both machines running NoMercy MediaServer (same version recommended — the
  task wire format is versioned but hasn't stabilised yet).
- Network reachability in both directions: coordinator → worker (task dispatch)
  and worker → coordinator (heartbeats + progress + optional source fetch).
- A **shared signing key** — a random string of at least 32 characters.
  Generate one with `openssl rand -base64 32` or equivalent. Both machines
  need the identical value. A shorter key is refused: distributed encoding
  stays off and the server log says why.
- Clocks within 5 minutes of each other (any NTP-synced machine is). A signed
  request whose timestamp is more than 5 minutes off, either way, is refused.
- **Either** a shared filesystem visible from both machines (NAS, SMB, NFS)
  **or** HTTP reachability from the worker back to the coordinator so it can
  stream source files.

## Coordinator config

Set these in the `Encoder` section of `appsettings.json`, or as environment
variables with a double underscore (`Encoder__DistributedEncodingSigningKey`).
The server reads `DistributedEncodingSigningKey`, `CoordinatorUrl`,
`WorkerSelfBaseUrl`, `WorkerId` and `WorkerHeartbeatInterval` from there:

```json
{
  "Encoder": {
    "DistributedEncodingSigningKey": "<your-random-32-byte-base64-string>"
  }
}
```

Restart the server. The dashboard's `GET /api/v1/dashboard/workers` endpoint
now returns `"distribution_enabled": true` and an empty worker list.

## Worker config

The remote worker is the same NoMercy MediaServer binary with distribution
settings that point at the coordinator:

```json
{
  "Encoder": {
    "DistributedEncodingSigningKey": "<same-key-as-coordinator>",
    "CoordinatorUrl": "https://your-coordinator.example.com:7626",
    "WorkerSelfBaseUrl": "https://this-worker.example.com:7626",
    "WorkerId": "beast-unit",
    "WorkerHeartbeatInterval": "00:00:20"
  }
}
```

- `CoordinatorUrl` — how the worker reaches the coordinator. Required.
- `WorkerSelfBaseUrl` — how the coordinator reaches back to this worker. The
  coordinator POSTs encode tasks to `{WorkerSelfBaseUrl}/api/v1/worker/execute-task`.
- `WorkerId` — defaults to `Environment.MachineName`. Override when running
  multiple workers on the same host. Only the letters `A-Z`/`a-z`, the
  digits, `.`, `_` and `-` are allowed; with anything else the worker does
  not register and logs a warning.
- `WorkerHeartbeatInterval` — default 20s. Kept between 1s and 30s (a value
  outside that range is moved to the nearest limit, with a warning): the
  coordinator drops a worker after 60s without a heartbeat.

On boot the worker's `WorkerSelfRegistrationService`:

1. POSTs `/api/v1/distribution/workers/register` to the coordinator with its
   hardware capabilities. Every call is signed with the shared key
   (`X-NoMercy-Timestamp` and `X-NoMercy-Signature`); the signature is the
   worker's only credential, it needs no user token.
2. Heartbeats every `WorkerHeartbeatInterval` to stay active.
3. DELETEs its registration when the process shuts down cleanly. If the
   process crashes, the coordinator evicts the stale entry after ~60s.

## Verifying the link

On the coordinator:

```sh
curl -H "Authorization: Bearer $YOUR_TOKEN" \
     https://your-coordinator/api/v1/dashboard/workers
```

Should return your worker in the `data` array with `"status": "active"`:

```json
{
  "distribution_enabled": true,
  "count": 1,
  "active_count": 1,
  "data": [
    {
      "worker_id": "beast-unit",
      "available_cpu_threads": 24,
      "available_gpu_slots": 12,
      "cpu_cores": 24,
      "gpu_count": 1,
      "status": "active",
      "last_seen_utc": "2026-04-17T...",
      "consecutive_failures": 0,
      "cooldown_until_utc": null
    }
  ]
}
```

## Source files: shared storage vs. HTTP fetch

The coordinator dispatches tasks containing absolute source paths. Workers
handle two layouts:

### Shared filesystem (home server setup)

Both machines see the library on the same mount point (`/mnt/media` or
`Z:\`). The worker's source fetcher returns the original path unchanged —
`File.Exists` passes, ffmpeg reads from the shared mount, zero network
transfer. **Recommended** whenever you control the network: simpler,
faster, no extra disk on the worker.

### HTTP fetch (WAN setup, cloud worker, etc.)

When the worker can't see the source path:

1. Worker checks `File.Exists(InputPath)` → false.
2. Worker computes a signed download URL:
   `GET /api/v1/worker-source?path={path}&ts={now}&sig={hmac(path|ts, key)}`.
3. Coordinator verifies the signature, checks the path is a known
   `VideoFile` in the library (allowlist), streams the file.
4. Worker writes to the task-scoped cache under
   `{LiveTranscodeCachePath}/remote-sources/{task-id}.{ext}`.
5. Worker rewrites the task's ffmpeg args to point at the cached path.
6. After the encode completes, the worker releases the cached file.

Retries reuse the cached download — no double transfer. Multi-GB sources
stream; nothing is loaded into memory.

**Security of the source endpoint:**

- HMAC-SHA256 signature over `{path}|{timestamp}`.
- Timestamp must be within 5 minutes of `now` (replay guard).
- Path must correspond to a known `VideoFile` in the coordinator's library
  — an attacker with the signing key can't turn `/etc/passwd` into a
  download.

## Progress visibility

During an encode, the remote worker POSTs progress snapshots every 2 seconds
to `/api/v1/dashboard/workers/{id}/tasks/{taskId}/progress`. The coordinator
caches them in memory with 15-minute stale eviction and exposes:

```sh
curl /api/v1/dashboard/workers/tasks/progress
```

→ live progress for all remote tasks. The dashboard consumes this to render
per-task progress bars.

Progress push is fire-and-forget; a coordinator outage doesn't block the
encode. Progress payloads carry no secrets so the progress endpoint is
unauthenticated (the signing key still guards task dispatch and source
fetch).

## Failure handling

**Worker task failure** — the dispatcher tries the next-best remote worker
before falling back to local. `MaxRemoteAttempts` = 2 (one retry). If all
remotes exhaust, the coordinator runs the task locally so the user's job
still completes.

**Worker health tracking** — three consecutive task failures put a worker
in a 2-minute cooldown. The dispatcher skips cooled workers; the dashboard
still shows them with `"status": "cooldown"` and the cooldown expiry time.
Re-registration explicitly clears the cooldown — useful when you restart a
misbehaving worker.

**Signing key mismatch** — HMAC verification fails, coordinator refuses the
response, task falls back to local. Both sides log the mismatch.

**Network partition** — heartbeats stop, worker goes stale after 60s and
drops out of the registry. When the network recovers, the worker's
self-registration loop re-registers automatically on the next heartbeat
attempt.

## Scaling hints

- The `WorkerAssigner` load-balances by `SpeedMultiplier × AvailableSlots`.
  Workers with more CPU + GPU capacity get more work. If your workers are
  wildly different speeds, the capacity-weighted split keeps the fast box
  from idling.
- `QualityVariant` tasks (one per ABR rung) are heavier than `TimeChunk`
  tasks (same rung across time ranges). The assigner schedules variants
  onto fast workers first.
- For very large worker farms (>10 workers), swap
  `InMemoryRemoteWorkerRegistry` for a persistent backing store in DI —
  the in-memory registry loses state on coordinator restart.

## Known limitations

- **No mTLS**. HMAC-signed payloads are the full security story. Works on
  trusted LAN + HTTPS to the coordinator; WAN deployments should add a
  VPN or TLS client-cert layer externally.
- **No retry backoff**. First worker fails, second worker is picked
  immediately. If all are flaky, failure cascades through the retry chain
  in seconds. Tune `MaxRemoteAttempts` (code constant) or swap in a
  plugin dispatcher with exponential backoff.
- **Source fetch is not resumable between processes**. A worker crash
  mid-download discards the partial file; the next task attempt re-downloads
  from scratch. HTTP Range requests are enabled server-side so a
  partial-fetch client could resume, but the current client streams straight
  to disk without checkpoint state.

## Troubleshooting

**Worker shows as "cooldown" after a bad task, I fixed the issue and want it back in rotation.**
Restart the worker process — it re-registers on boot with a fresh counter, which
clears the cooldown.

**Coordinator returns 503 on register/heartbeat.**
The coordinator's `DistributedEncodingSigningKey` isn't set. Check
`appsettings.json` on the coordinator and restart.

**Coordinator returns 401 on register/heartbeat.**
The signature did not verify: the keys differ, or the two clocks are more
than 5 minutes apart. A worker whose `CoordinatorUrl` is not an absolute
`http(s)://` URL does not try at all and logs a warning instead.

**Worker registers successfully but tasks all fall back to local.**
Signing keys differ between coordinator and worker. Every task payload
fails HMAC verification on the worker side, and every result fails on the
coordinator side. Confirm the key string is byte-for-byte identical on both
machines (watch for quotes / trailing whitespace in the config file).

**Worker's GPU never gets used.**
Check `/api/v1/dashboard/workers` — `available_gpu_slots` should be
non-zero. If it's zero the worker didn't detect the GPU; check the
`HardwareBenchmarkHostedService` logs on the worker for CUDA/QSV/VAAPI
init errors.
