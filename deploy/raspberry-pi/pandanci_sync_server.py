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
                    PRIMARY KEY (kind, record_key)
                )
                """
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

    def _merge_records(self, connection, kind, records, packet_device_id, current_records):
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

            current = current_records.get((kind, key))
            if current is not None and (ticks, device_id) <= current:
                continue
            payload = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
            updates.append((kind, key, ticks, device_id, payload))
            current_records[(kind, key)] = (ticks, device_id)

        if updates:
            connection.executemany(
                """
                INSERT INTO sync_records(kind, record_key, updated_at_ticks, device_id, payload)
                VALUES(?, ?, ?, ?, ?)
                ON CONFLICT(kind, record_key) DO UPDATE SET
                    updated_at_ticks=excluded.updated_at_ticks,
                    device_id=excluded.device_id,
                    payload=excluded.payload
                """,
                updates,
            )

    def merge(self, packet):
        if not isinstance(packet, dict):
            raise ValueError("Request must be a JSON object")
        packet_device_id = str(packet.get("DeviceId") or "unknown")
        with self.lock:
            with closing(self._connect()) as connection:
                connection.execute("BEGIN IMMEDIATE")
                current_records = {
                    (str(kind), str(key)): (int(ticks), str(device_id))
                    for kind, key, ticks, device_id in connection.execute(
                        "SELECT kind, record_key, updated_at_ticks, device_id FROM sync_records"
                    )
                }
                self._merge_records(connection, "word", packet.get("Words"), packet_device_id, current_records)
                self._merge_records(connection, "note", packet.get("Notes"), packet_device_id, current_records)
                connection.commit()
                rows = connection.execute(
                    "SELECT kind, payload FROM sync_records ORDER BY kind, record_key"
                ).fetchall()

        response = {"DeviceId": "pandanci-pi", "Words": [], "Notes": []}
        for kind, payload in rows:
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
