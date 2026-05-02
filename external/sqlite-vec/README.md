# sqlite-vec native binaries

This directory contains pre-built `vec0` SQLite extension binaries vendored from
[asg017/sqlite-vec](https://github.com/asg017/sqlite-vec).

## Release

- **Version:** v0.1.9
- **Release date:** 2026-03-31
- **Source:** https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9
- **Upstream checksums:** https://github.com/asg017/sqlite-vec/releases/download/v0.1.9/checksums.txt

## Files

| Path | Source archive | SHA256 (binary) |
| --- | --- | --- |
| `runtimes/win-x64/native/vec0.dll` | `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz` | `fcf98662a7ad9dce394b96a88f91032047823831b951c76636787c312a6476e6` |
| `runtimes/linux-x64/native/vec0.so` | `sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz` | `5923730861b86c707cca5602b5f91092f9e52a46706dbc6e269fd4bb9c4498e8` |
| `runtimes/osx-arm64/native/vec0.dylib` | `sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz` | `193e480c50b59a55977d166f4aaf0e1bc8832d6963516e5950f39e4d2ce0b793` |

The archive checksums match the upstream `checksums.txt` for v0.1.9:

| Archive | Upstream SHA256 |
| --- | --- |
| `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz` | `51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983` |
| `sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz` | `b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7` |
| `sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz` | `8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29` |

## Updating

1. Pick a release from https://github.com/asg017/sqlite-vec/releases.
2. `gh release download <ver> --repo asg017/sqlite-vec --pattern "sqlite-vec-<ver>-loadable-windows-x86_64.tar.gz" --pattern "sqlite-vec-<ver>-loadable-linux-x86_64.tar.gz" --pattern "sqlite-vec-<ver>-loadable-macos-aarch64.tar.gz" --pattern "checksums.txt"`.
3. Verify each archive against `checksums.txt`.
4. `tar -xf` each archive and replace the corresponding file under `runtimes/`.
5. Update this README with the new version and SHA256s.

## License

The `vec0` binaries are distributed under the upstream sqlite-vec license
(Apache 2.0 / MIT dual). See https://github.com/asg017/sqlite-vec for details.
