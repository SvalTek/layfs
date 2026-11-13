# layfs

A layered (union) file system for Windows, built on [WinFsp](https://winfsp.dev/).

`layfs` presents a single drive letter that merges two directories:

* a **writable layer** (`-w`) that holds every change, and
* a **read-only layer** (`-r`) that supplies the base content.

The writable layer is an overlay on top of the read-only layer. Lookups check the
writable layer first and fall back to the read-only layer, so a file that only exists
below appears to exist at the mount point. The first write to a lower-layer file copies
it up into the writable layer (copy-on-write) and all further changes land there, leaving
the original untouched.

> This started life as a proof of concept. It works for the common cases, but the
> overlay semantics are still incomplete — see [Current limitations](#current-limitations).

## Requirements

* Windows
* [WinFsp](https://winfsp.dev/rel/) installed (the `winfsp-msil` managed assembly is
  referenced from the WinFsp installation directory)
* .NET 10 SDK

## Build

```powershell
dotnet build -c Release
```

The project locates `winfsp-msil.dll` under `%ProgramFiles%\WinFsp\bin` or
`%ProgramFiles(x86)%\WinFsp\bin`. If WinFsp lives somewhere else, point the build at it:

```powershell
dotnet build -c Release -p:WinFspDir="D:\Tools\WinFsp\bin\"
```

The build fails early with a clear message when WinFsp cannot be found.

## Usage

```powershell
layfs -w <write-dir> -r <read-only-dir> -m <mount-point>
```

| Option | Meaning |
| --- | --- |
| `-m <mount-point>` | Where to mount. Use a **drive letter** such as `L:`. |
| `-w <directory>` | Writable layer directory. Created content and copy-on-write files go here. |
| `-r <directory>` | Read-only layer directory. |
| `-u <UNC>` | Mount as a UNC path. |
| `-d [flags]` | Enable WinFsp debug output (`-1` enables everything). |
| `-D <logfile>` | WinFsp debug log file. |
| `-?` | Show usage. |

Example:

```powershell
layfs -w C:\layers\upper -r C:\layers\lower -m L:
```

Then use `L:` like any other drive. Unmount by stopping the process (or with
`net use L: /delete`).

### Mount points

Drive letters are the tested and recommended mount point. On some WinFsp builds,
mounting onto an existing directory fails with `STATUS_OBJECT_NAME_COLLISION`
(`0xC0000035`); that is a WinFsp limitation, not a `layfs` one — WinFsp's own sample
file systems behave the same way.

## Debugging

`-d` turns on file system tracing (`-d -1` for maximum verbosity). Because WinFsp holds
its own `-D` log file open exclusively, `layfs` writes its trace to a sibling file:

| Invocation | `layfs` trace destination |
| --- | --- |
| `-d -1` (no `-D`) | `%TEMP%\layfs-trace.log` |
| `-d -1 -D C:\logs\fsp.log` | `C:\logs\fsp.log.layfs` |
| `-d -1 -D -` | standard error |

Every callback is traced with its arguments and NT status, including the layer a name
resolved against, so you can see exactly why an operation was refused.

## Current limitations

These are known gaps in the overlay logic, not bugs in the plumbing:

* **No whiteouts.** Deleting an entry that only exists in the read-only layer is refused
  with `ACCESS_DENIED`, and deleting a copied-up file lets the lower-layer original
  reappear. A proper union file system needs tombstones to hide lower entries.
* **Renames are write-layer only.** Renaming an entry that has not been copied up yet
  fails.
* `IndexNumber` and `HardLinks` in file information are always reported as `0`.
* Only two layers are supported today; multiple ordered read layers are planned.
