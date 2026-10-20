#!/usr/bin/env python3
import argparse
import json
import socketserver
import sqlite3
import threading
from contextlib import closing

MAX_REQUEST_BYTES = 16 * 1024 * 1024


class SyncStore:
    def __init__(self, database_path):
        self.database_path = database_path
        self.lock = threading.Lock()
        self._initialize()

    def _connect(self):
        connection = sqlite3.connect(self.database_path, timeout=15)
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA synchronous=FULL")
        return connection

    def _initialize(self):
        with closing(self._connect()) as connection:
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS sync_records (
                    kind TEXT NOT NULL,
                    record_key TEXT NOT NULL,
                    updated_at_ticks INTEGER NOT NULL,
                    device_id TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (kind, record_key)
                )
                """
            )
            columns = {
                str(row[1]) for row in connection.execute("PRAGMA table_info(sync_records)")
            }
            if "revision" not in columns:
                connection.execute(
                    "ALTER TABLE sync_records ADD COLUMN revision INTEGER NOT NULL DEFAULT 0"
                )
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS sync_state (
                    id INTEGER PRIMARY KEY CHECK(id=1),
                    current_revision INTEGER NOT NULL
                )
                """
            )
            connection.execute(
                "UPDATE sync_records SET revision=rowid WHERE revision=0"
            )
            max_revision = int(
                connection.execute(
                    "SELECT COALESCE(MAX(revision), 0) FROM sync_records"
                ).fetchone()[0]
            )
            connection.execute(
                """
                INSERT INTO sync_state(id, current_revision) VALUES(1, ?)
                ON CONFLICT(id) DO UPDATE SET
                    current_revision=MAX(sync_state.current_revision, excluded.current_revision)
                """,
                (max_revision,),
            )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_sync_records_revision ON sync_records(revision)"
            )
            connection.commit()

    @staticmethod
    def _normalize_word(value):
        value = str(value or "").strip()
        value = value.strip(" \t\r\n.,;:!?\"'()[]{}").lower()
        return value[:80]

    @staticmethod
    def _safe_ticks(value):
        try:
            return max(0, int(value))
        except (TypeError, ValueError):
            return 0

    def _merge_records(
        self,
        connection,
        kind,
        records,
        packet_device_id,
        current_records,
        requested_keys,
        current_revision,
    ):
        updates = []
        for record in records or []:
            if not isinstance(record, dict):
                continue
            if kind == "word":
                key = self._normalize_word(record.get("Word"))
                if not key:
                    continue
                record["Word"] = key
            else:
                key = str(record.get("Id") or "").strip()[:128]
                if not key:
                    continue
                record["Id"] = key
            record["Deleted"] = bool(record.get("Deleted", False))

            ticks = self._safe_ticks(record.get("UpdatedAtTicks"))
            device_id = str(record.get("DeviceId") or packet_device_id or "unknown")[:128]
            record["UpdatedAtTicks"] = ticks
            record["DeviceId"] = device_id
            record_key = (kind, key)
            requested_keys.add(record_key)

            current = current_records.get(record_key)
            if current is not None and (ticks, device_id) <= current[:2]:
                continue
            current_revision += 1
            payload = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
            updates.append((kind, key, ticks, device_id, payload, current_revision))
            current_records[record_key] = (ticks, device_id, current_revision, payload)

        if updates:
            connection.executemany(
                """
                INSERT INTO sync_records(kind, record_key, updated_at_ticks, device_id, payload, revision)
                VALUES(?, ?, ?, ?, ?, ?)
                ON CONFLICT(kind, record_key) DO UPDATE SET
                    updated_at_ticks=excluded.updated_at_ticks,
                    device_id=excluded.device_id,
                    payload=excluded.payload,
                    revision=excluded.revision
                """,
                updates,
            )
        return current_revision

    def merge(self, packet):
        if not isinstance(packet, dict):
            raise ValueError("Request must be a JSON object")
        packet_device_id = str(packet.get("DeviceId") or "unknown")
        since_revision = self._safe_ticks(packet.get("SinceRevision"))
        with self.lock:
            with closing(self._connect()) as connection:
                connection.execute("BEGIN IMMEDIATE")
                current_revision = int(
                    connection.execute(
                        "SELECT current_revision FROM sync_state WHERE id=1"
                    ).fetchone()[0]
                )
                if since_revision > current_revision:
                    since_revision = 0
                current_records = {
                    (str(kind), str(key)): (int(ticks), str(device_id), int(revision), str(payload))
                    for kind, key, ticks, device_id, revision, payload in connection.execute(
                        "SELECT kind, record_key, updated_at_ticks, device_id, revision, payload FROM sync_records"
                    )
                }
                requested_keys = set()
                current_revision = self._merge_records(
                    connection,
                    "word",
                    packet.get("Words"),
                    packet_device_id,
                    current_records,
                    requested_keys,
                    current_revision,
                )
                current_revision = self._merge_records(
                    connection,
                    "note",
                    packet.get("Notes"),
                    packet_device_id,
                    current_records,
                    requested_keys,
                    current_revision,
                )
                connection.execute(
                    "UPDATE sync_state SET current_revision=? WHERE id=1",
                    (current_revision,),
                )
                connection.commit()
                rows = sorted(
                    (kind, key, values[3])
                    for (kind, key), values in current_records.items()
                    if values[2] > since_revision or (kind, key) in requested_keys
                )

        response = {
            "DeviceId": "pandanci-pi",
            "SinceRevision": since_revision,
            "ServerRevision": current_revision,
            "Words": [],
            "Notes": [],
        }
        for kind, _key, payload in rows:
            record = json.loads(payload)
            response["Words" if kind == "word" else "Notes"].append(record)
        return response


class SyncRequestHandler(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.settimeout(120)
        chunks = []
        size = 0
        while True:
            chunk = self.request.recv(65536)
            if not chunk:
                break
            size += len(chunk)
            if size > MAX_REQUEST_BYTES:
                raise ValueError("Request is too large")
            chunks.append(chunk)
        if not chunks:
            return
        packet = json.loads(b"".join(chunks).decode("utf-8"))
        response = self.server.store.merge(packet)
        data = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        try:
            self.request.sendall(data)
        except (BrokenPipeError, ConnectionResetError):
            return


class ThreadedSyncServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, address, store):
        self.store = store
        super().__init__(address, SyncRequestHandler)


def main():
    parser = argparse.ArgumentParser(description="Pandanci LAN sync server")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=17890)
    parser.add_argument("--database", default="pandanci.db")
    args = parser.parse_args()

    store = SyncStore(args.database)
    with ThreadedSyncServer((args.host, args.port), store) as server:
        print("Pandanci sync server listening on %s:%d" % (args.host, args.port), flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
