# pg_dbbackup .NET integration tests

xUnit tests that build the extension inside a `postgres:17` container and run it.

## Prerequisites

- .NET 8 SDK (or newer)
- A working Docker or Podman socket reachable as `docker.exe` / via `DOCKER_HOST`.
  On Windows with Podman, the bundled `docker.exe` shim and default machine work out of the box.

## Run

```
cd tests/pg_dbbackup.Tests
dotnet test
```

The first run pulls `postgres:17` and installs build tools inside the container,
so expect ~1–2 minutes of warm-up before the first test starts.
