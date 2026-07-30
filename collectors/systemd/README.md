# Freeboard collectors: systemd

The host runtime for a collector. Use it where the evidence is the host - disk
encryption, patch level, local agent state - or where the collector must run
from a specific machine's network position. Use `collectors/docker/` or
`collectors/function/` for anything that only reaches a vendor API.

There is no separate wrapper here: the units run the same
`collectors/docker/entrypoint.sh`, which is plain POSIX shell and needs only
`curl` and `jq`. The collector script, the environment contract, and the retry
and exit-code behaviour are exactly as documented in
`collectors/docker/README.md`. The request contract is `docs/evidence-ingest.md`.

## Install

One collector per template instance; the instance name is the collector id.

```sh
# Wrapper and collector script.
install -D -m 0755 collectors/docker/entrypoint.sh /usr/local/lib/freeboard/entrypoint.sh
install -D -m 0755 <your-collect.sh>               /usr/local/lib/freeboard/collect.sh

# Units.
install -D -m 0644 collectors/systemd/freeboard-collector@.service \
    /etc/systemd/system/freeboard-collector@.service
install -D -m 0644 collectors/systemd/freeboard-collector@.timer \
    /etc/systemd/system/freeboard-collector@.timer

# Per-collector configuration. It holds the bearer token, so root-owned and 0600.
install -d -m 0755 /etc/freeboard/collectors
install -m 0600 /dev/null /etc/freeboard/collectors/google-workspace-mfa.env

systemctl daemon-reload
systemctl enable --now freeboard-collector@google-workspace-mfa.timer
```

`/etc/freeboard/collectors/<collector-id>.env`:

```sh
FREEBOARD_BASE_URL=https://freeboard.example
FREEBOARD_INGEST_TOKEN=v1.<secret>
FREEBOARD_ORGANISATION_ID=org-acme
FREEBOARD_REQUIREMENT_ID=req-mfa
FREEBOARD_COLLECTOR_SCRIPT=/usr/local/lib/freeboard/collect.sh
```

Do not set `FREEBOARD_COLLECTOR_ID` here: the unit derives it from the instance
name so the two cannot drift. A collector id containing `/` or other characters
systemd escapes must be passed through `systemd-escape` to form the instance
name.

## Schedule

The shipped default is hourly with up to 5 minutes of jitter. Override per
collector - the timer must fire at least as often as the collector's staleness
window, or Freeboard will mark it stale between good runs:

```sh
systemctl edit freeboard-collector@google-workspace-mfa.timer
```

```ini
[Timer]
# A drop-in adds to a list, so clear the inherited value before setting a new one.
OnCalendar=
OnCalendar=*-*-* 06,18:00:00
```

`Persistent=true` means a host that was powered off through a tick collects once
on boot, so downtime does not read as a stale collector.

## Privileged collectors

The service runs under `DynamicUser=yes` with the filesystem read-only and no
capabilities, which suits a collector that only calls a vendor API. A collector
that inspects the host needs a drop-in granting exactly what it reads and no
more:

```sh
systemctl edit freeboard-collector@endpoint-audit.service
```

```ini
[Service]
DynamicUser=no
User=root
CapabilityBoundingSet=CAP_SYS_ADMIN
```

Grant the narrowest thing that works: a specific capability before `User=root`,
and `ReadOnlyPaths=` before relaxing `ProtectSystem=`.

## Operate

```sh
systemctl list-timers 'freeboard-collector@*'          # next and last fire times
systemctl status freeboard-collector@<id>.service      # last run result
journalctl -u freeboard-collector@<id>.service -n 50   # wrapper output
systemctl start freeboard-collector@<id>.service       # run once, now
```

The wrapper exits `0` only on `200`/`201`, so a failed unit is a failed ingest.
Deterministic rejections (`400/401/403/409/413/422`) are not retried; transient
failures retry inside the run with the identical body, under the same `run_id`,
so a retry dedupes. The units add no `Restart=`: a run that fails outright is
retried at the next timer tick, and because the collector script generates a
fresh `run_id` that is a new run rather than a replay.

Alert on unit failure with `systemctl --failed` or an `OnFailure=` drop-in.
Freeboard's own staleness evaluation catches a collector that stops reporting,
but only after the staleness window has elapsed.
