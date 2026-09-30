<!-- This specification is dedicated to the public domain under CC0 1.0. -->

# Brick registry, version 1

A registry is a static site. Anything that can serve files can host one: GitHub Pages, an object store, a folder in a
repository, a directory on a USB stick. There is no server to run and no API beyond reading files.

The address a host is given (`https://bricks.mass4.org/v1`) is the **location**. Everything below is relative to it.
The location's path shape, `/v1/index.json`, is the one permanent commitment; files behind it can move.

```
v1/index.json                                 the index
v1/keys                                       the registry's public keys
v1/bricks/<id>/<version>/<id>-<version>.brick  the brick files (any path works; the index says where)
```

## Names

A brick id is lowercase reverse-DNS: `org.mass4.turian.ui`, `com.acme.inventory`. Publishers without a domain use
`user.<name>.<brick>`; the first publish under `user.<name>` claims it.

## `index.json`

```json
{
  "format": 1,
  "generatedAt": "2026-10-01T12:00:00+00:00",
  "claims": { "user.mateo": { "key": "SHA256:…" } },
  "bricks": {
    "user.mateo.rules": {
      "versions": {
        "1.0.0": {
          "url": "bricks/user.mateo.rules/1.0.0/user.mateo.rules-1.0.0.brick",
          "integrity": "sha256-<base64>",
          "signature": "<base64 of the registry's SSH signature>",
          "signedBy": "SHA256:<fingerprint of the registry key>",
          "publisherSignature": "<base64 of the publisher's SSH signature>",
          "publisherKey": "ssh-ed25519 AAAA… mateo",
          "publishedAt": "2026-10-01T12:00:00+00:00",
          "dependencies": { "user.mateo.core": "^1.0.0" },
          "engines": { "turian": ">=1.0 <2" },
          "store": { "entitlement": "token", "eula": "https://…", "redistribute": false, "embeddable": false },
          "yanked": false
        }
      }
    }
  }
}
```

- `url` is relative to the location, or an absolute `https://` address.
- `integrity` is the `.brick` file's SHA-256, `sha256-` and base64. A client checks it before using the file.
- `dependencies`, `engines` and `store` repeat the brick's `package.json`, so a client can choose a version without
  downloading it.
- `yanked` hides a version from new resolutions. It stays downloadable, so a project that locked it keeps working.
- Versions are immutable. A version is published once; fixing it means publishing the next one.

## Signatures

Signatures are OpenSSH signatures of ed25519 keys (`ssh-keygen -Y sign`), so publishers use keys and tools they already
have, and anyone can check them with `ssh-keygen -Y check-novalidate` or any SSHSIG implementation.

The signed message is, as UTF-8:

```
<id>@<version>\n<integrity>\n
```

| Signature | Namespace (`-n`) | Made by | Checked by |
|---|---|---|---|
| `signature` | `bricks@mass4.org` | the registry | hosts: **required**, against keys the project trusts |
| `publisherSignature` | `brick-publisher@mass4.org` | the publisher | hosts: checked against `publisherKey` when present |

`signature` and `publisherSignature` hold the base64 body of the armored signature, without the
`-----BEGIN SSH SIGNATURE-----` lines. `signedBy` and claims use the fingerprint `ssh-keygen -l` prints.

A host **never learns which keys to trust from the registry**. The keys a project trusts for a registry are part of
the project's own `Bricks/manifest.json` (`scopedRegistries[].keys`), or built into the host for the registry it
ships with. `keys` is published so people can copy the key out of band.

## Claims

`claims` maps a name prefix to the fingerprint of the publisher key that owns it. A publish under a claimed prefix must
carry a publisher signature by that key. A `user.<name>` prefix is claimed by the first publish under it; any other
prefix is claimed explicitly by the registry's operator. Claims record ownership for humans and tools; what a host
enforces is the registry signature.

## `keys`

```json
{ "keys": [ { "id": "SHA256:…", "publicKey": "ssh-ed25519 AAAA… registry", "scopes": [] } ] }
```

## Access tokens

A version with `"store": { "entitlement": "token" }` is downloaded with `Authorization: Bearer <token>`. The token comes
from `GAYA_REGISTRY_TOKEN_<NAME>` (the registry's name, upper-cased, other characters `_`) or from
`~/.gaya/credentials.json`, which maps a registry name or url to a token. Tokens are never written to a manifest or a
lock file, and are sent only to the registry's own host.

## Hosts

A project chooses registries with `scopedRegistries` in `Bricks/manifest.json`:

```json
{
  "dependencies": { "user.mateo.rules": "^1.0.0" },
  "scopedRegistries": [
    { "name": "studio", "url": "https://bricks.studio.example/v1", "scopes": ["com.studio"], "keys": ["ssh-ed25519 AAAA… studio"] }
  ]
}
```

A version range with no other source is looked up in the registry whose scope covers the name: the newest release that
satisfies every range asked for and runs on the host's engine. The lock file records the version, the file's hash and
the fingerprint of the signing key; later installs must match all three.
