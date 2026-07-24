# Security policy

## Supported versions

Speedysearch is an early preview. Security fixes are applied to the latest
`0.2.x` release only.

| Version | Supported |
| --- | --- |
| `0.2.x` | Yes |
| `< 0.2` | No |

## Reporting a vulnerability

Please report suspected vulnerabilities privately through the repository's
**Security → Report a vulnerability** page (GitHub Private Vulnerability
Reporting). Do not include sensitive details in a public issue.

Include:

- Affected version and operating system.
- Reproduction steps or a minimal proof of concept.
- Expected impact.
- Any suggested mitigation.

You should receive an acknowledgement within seven days. Maintainers will
investigate, coordinate a fix and disclosure when appropriate, and credit
reporters who want attribution.

## Sensitive areas

Please take extra care when reviewing:

- Result-opening commands and ID validation.
- Tauri capabilities and local asset access.
- Global-shortcut and COSMIC configuration changes.
- Install, migration, and uninstall scripts.
- Index, clickstream, configuration, and model file permissions.

Never attach real clickstreams, search indexes, or private filesystem listings
to a report. Create a minimal synthetic reproduction instead.
