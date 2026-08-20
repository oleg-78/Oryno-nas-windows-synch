# Oryno Sync Server Contract Used By Windows W.1

Source: `oleg-78/oryno-nas@6610236`, `backend/app/modules/sync/router.py`, `schemas.py`, `models/sync.py`, and `docs/ORYNO_SYNC_ARCHITECTURE.md`.

`POST /api/sync/devices` is protected by the browser cookie/admin dependency. Request body:

```json
{"device_name":"DESKTOP-ORYNO","platform":"windows"}
```

The response contains `device_id`, `device_name`, `token`, `token_hash`, and `session_id`; plaintext `token` is returned only during registration. Client endpoints authenticate with `Authorization: Bearer <token>`. Invalid/revoked sessions return HTTP 401 with `DEVICE_TOKEN_INVALID` or `DEVICE_REVOKED`.

Metadata endpoints:

```text
GET /api/sync/roots
GET /api/sync/roots/{root_id}/items?cursor={opaque}&limit={1..2000}
GET /api/sync/roots/{root_id}/changes?after={revision}&limit={1..2000}
```

Inventory response is `{anchor_revision,items,next_cursor}`. Item fields are `item_id`, `parent_item_id`, `name`, `relative_path`, `item_type`, `size_bytes`, `mtime_utc`, `content_hash`, and `version`.

Changes response is `{changes,next_revision,has_more}`. Change fields are `revision`, `change`, `item_id`, `version`, `parent_item_id`, `name`, `relative_path`, `item_type`, `size_bytes`, `mtime_utc`, `content_hash`, and `source_type`. S.1 change values are `CREATE`, `UPDATE`, `MOVE`, and `DELETE`.

Errors are flat `{ok:false,code,message}`. Cursor expiry is HTTP 410 with `SYNC_CURSOR_EXPIRED` and `full_resync_required=true`. S.1 does not provide content download, upload, or client mutation routes; those capabilities remain blocked until S.2.
