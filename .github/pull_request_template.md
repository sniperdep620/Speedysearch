## Summary

<!-- What changed, and why? -->

## Validation

<!-- List exact commands and results. -->

- [ ] `npm run check`
- [ ] `.\windows-native\test.ps1 -Configuration Release` (for Windows changes)
- [ ] `./scripts/check.sh --with-e2e` (for interaction changes)
- [ ] Benchmarks included (for ranking/indexing changes)

## Impact

<!-- Note UI screenshots, platform differences, permissions, configuration,
persistence-format, privacy, or performance impact. Write "None" where relevant. -->

## Checklist

- [ ] Tests cover the behavior where practical.
- [ ] Documentation and `CHANGELOG.md` are updated.
- [ ] No private index, clickstream, model, configuration, or path data is included.
- [ ] Security-sensitive changes preserve validation and least privilege.
