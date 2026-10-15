# Resona Cloud

The compose stack runs PostgreSQL, the Resona ASP.NET Core API, and the Python/Essentia analysis API. Both APIs are bound to localhost so a reverse proxy can provide public HTTPS without exposing the container ports directly.

1. Copy `.env.example` to `.env` and replace the database password.
2. Run `docker compose up -d --build` from this directory.
3. Verify both services:
   - `curl http://127.0.0.1:5080/health`
   - `curl http://127.0.0.1:5081/health`
4. Put Caddy, nginx, or another TLS reverse proxy in front of ports 5080 and 5081. Separate subdomains such as `api.example.com` and `analyzer.example.com` keep the proxy configuration simple.

For nginx, the API virtual host must allow the same 256 MB per-track request size as the ASP.NET Core server. Add this inside the `server` block for the Resona API and reload nginx:

```nginx
client_max_body_size 256m;
```

Without this setting nginx uses its much smaller default limit and full-library synchronization returns HTTP 413 (`Request Entity Too Large`).

## Public library API

The public read endpoints require no device credentials. List responses are ordered deterministically and use `offset`/`limit` pagination. The default limit is 50 and the maximum is 100.

```text
GET /api/v1/public/profiles?search=&offset=0&limit=50
GET /api/v1/public/profiles/{userId}
GET /api/v1/public/profiles/{userId}/image
GET /api/v1/public/profiles/{userId}/tracks?search=&offset=0&limit=50
```

After deploying, these commands provide a quick production check:

```bash
curl -sS 'https://api.resona-music.de/api/v1/public/profiles?limit=10'
curl -sS 'https://api.resona-music.de/api/v1/public/profiles/USER_ID'
curl -sS 'https://api.resona-music.de/api/v1/public/profiles/USER_ID/tracks?limit=10'
curl -fS 'https://api.resona-music.de/api/v1/public/profiles/USER_ID/image' -o profile.jpg
```

Profile search matches username and bio. Track search matches edited title, original title, and channel name. Missing profiles return HTTP 404; invalid pagination returns HTTP 400.

The schema is created idempotently at API startup. PostgreSQL data lives in the `postgres-data` volume. Uploaded audio lives in the `resona-media` volume. Back up both volumes before upgrades; use `pg_dump` for PostgreSQL.

## Private device-library synchronization

The authenticated device API keeps the revisioned shared library, server-side download queue, and a persistent inventory of individually uploaded audio files:

```text
PUT /api/v1/device-library-snapshot
GET /api/v1/device-library-snapshot
GET /api/v1/library-media
PUT /api/v1/library-media/{trackKey}
GET /api/v1/library-media/{trackKey}
PUT /api/v1/device-library/tracks/{trackKey}
PUT /api/v1/device-library/presets
```

Audio uploads are atomic and recorded only after the complete file has been written and hashed. Downloads support HTTP range requests. The desktop compares its local library with `GET /api/v1/library-media` and uploads only missing files, so interrupted initial synchronization resumes on the next application start.

Track and preset updates require the revision last read by the client. Stale writes return HTTP 409 together with the current server value. YouTube downloads run only in the Windows application and enter its single persistent local queue.

The analyzer intentionally runs one analysis at a time by default because TensorFlow inference is memory intensive. It returns MAEST genre scores, BPM, EBU R128 loudness/dynamics, and the MIREX emotional-character clusters used by Resona. Increase `MAX_CONCURRENT_ANALYSES` only after checking memory usage on the server.

`MUSIC_API_KEY` is optional. When it is configured, enter the same value under **Settings → Servers → Analysis server → API key** in Resona Desktop. The client sends it as `X-Api-Key`. If no key is configured, keep the analyzer behind a trusted proxy, VPN, or restricted network instead of exposing it openly to the internet.

## Local HTTPS deployment

The compose stack includes an nginx proxy and CoreDNS for installations that use a locally trusted certificate. Both services use HTTPS port `443`; nginx routes `api.resona.home.arpa` to the cloud API and `analyzer.resona.home.arpa` to the analyzer. CoreDNS resolves these private names and forwards other queries to the local router. The backend HTTP ports `5080` and `5081` remain available only on the server loopback interface.

Place the server certificate and private key at `deploy/cloud/tls/server.crt` and `deploy/cloud/tls/server.key`. The certificate must contain every hostname used by clients as a Subject Alternative Name. Trust its issuing CA on each client device, configure `192.168.178.102` as the client's DNS server, then configure Resona with `https://api.resona.home.arpa` and `https://analyzer.resona.home.arpa`. Pasting either service's `/health` URL is also accepted by the desktop app and normalized to the base address.
