# Memcached Server in C#

A complete, from-scratch implementation of a Memcached-compatible server in C# on .NET 8 — the full text protocol, a thread-safe expiring store with LRU eviction, and a pipelined connection layer built on `System.IO.Pipelines`.

It speaks the real Memcached wire protocol, so any standard Memcached client (telnet included) can drive it.

Built following the [Coding Challenges](https://codingchallenges.fyi/challenges/challenge-memcached/) "Build Your Own Memcached Server" challenge — all steps, plus the operational extras a production cache needs.

## Why this project

Memcached looks simple from the outside — a key/value cache — but building one properly touches the core of backend engineering:

- **Networking** — a concurrent TCP server speaking a real wire protocol
- **Protocol parsing** — a line-based text protocol with strict reply formats
- **Concurrency** — many clients, one shared store, atomic read-modify-write commands
- **Cache behaviour** — expiry, size limits, LRU eviction, CAS-based optimistic concurrency

## Features

**Retrieval** — `get`, `gets` (multi-key, with CAS ids)

**Storage** — `set`, `add`, `replace`, `append`, `prepend`, `cas` — all with `noreply` support, flags, and full Memcached storage semantics (append/prepend keep the original flags and expiry, `cas` answers `EXISTS` / `NOT_FOUND` correctly)

**More commands** — `delete`, `incr` / `decr` (unsigned 64-bit, wrap on overflow, floor at zero, `CLIENT_ERROR` on non-numeric values), `touch`, `flush_all` (immediate and delayed), `version`, `stats`, `quit`

**Cache behaviour**
- Expiry per Memcached rules: `0` never expires, up to 30 days is relative seconds, larger values are absolute Unix time; lazy expiry on access plus a one-second background sweep
- Memory bound (default 64 MB) with LRU eviction and an `evictions` counter
- Per-item size limit (default 1 MB) → `SERVER_ERROR object too large for cache`
- Binary-safe values, pipelined commands, commands split across packets

**Operations** — a `stats` implementation with the counters you would expect (`cmd_get`, `get_hits`, `cas_badval`, `bytes`, `curr_items`, connection counts, bytes read/written…), structured console logging, and graceful shutdown via the .NET generic host.

## Running it

```bash
dotnet run --project src/MemcachedServer
# Memcached server listening on 127.0.0.1:11211
```

Options:

| Flag | Meaning | Default |
|------|---------|---------|
| `-p <port>` | TCP port to listen on | `11211` |
| `-m <megabytes>` | Maximum cache memory | `64` |
| `-I <bytes>` | Maximum size of a single value | `1048576` (1 MB) |

## Trying it out

```bash
$ telnet 127.0.0.1 11211
set greeting 0 0 5
hello
STORED
get greeting
VALUE greeting 0 5
hello
END
incr counter 1
NOT_FOUND
```

## Architecture

```
src/MemcachedServer/
    Program.cs                 Generic-host wiring: options → DI → hosted services
    Options/ServerOptions.cs   -p / -m / -I command-line configuration
    Transport/TcpServer.cs     Listening socket + accept loop (one session per connection)
    Protocol/MemcachedSession.cs   Pipelines framing: lines + data blocks, batched replies
    Protocol/CommandParser.cs      Command-line grammar → typed commands (pure, unit-tested)
    Protocol/CommandProcessor.cs   Command semantics → protocol replies, stats accounting
    Core/MemcachedStore.cs     The cache: striped locks, LRU list, byte ledger, expiry, CAS
    Core/CacheEntry.cs         One item (value + flags + expiry + cas id)
    Core/ExpiryCalculator.cs   exptime → absolute instant, per Memcached's rules
    Core/CacheStats.cs         Interlocked operational counters behind `stats`
    Hosting/                   Listener lifetime + the one-second maintenance sweep
tests/MemcachedServer.Tests/
    MemcachedStoreTests.cs     Store semantics: storage kinds, CAS, deltas, expiry, eviction, concurrency
    ProtocolTests.cs           End-to-end wire tests over an in-memory duplex transport
    CommandParserTests.cs      Grammar edge cases
```

Design notes worth knowing:

- **Concurrency** — operations on the same key serialise on one of 64 lock stripes (chosen by the key), so `incr` / `cas` / `append` are atomic per key without a global lock. Structural changes (map membership, LRU order, byte accounting) take one short-held gate, always acquired after a stripe, so lock ordering is deadlock-free. Value arrays are never mutated in place, so a value handed to a reader stays stable.
- **Framing** — the session consumes bytes only when a complete unit (a line, or a data block of the declared length + CRLF) is present, the same pattern Kestrel uses; pipelined commands are answered in batches with one flush.
- **Time** — everything time-based goes through `TimeProvider`, so expiry behaviour is tested with a fake clock instead of sleeps.

## Tests

```bash
dotnet test
```

64 tests cover the store semantics (including a concurrent-increment atomicity test: 8 workers × 250 increments must total exactly 2000), the parser grammar, and full wire-level sessions — exact reply bytes for every command, pipelining, split packets, bad data chunks, `noreply`, binary payloads, and stats accounting.

## License

MIT — see [LICENSE](LICENSE).
