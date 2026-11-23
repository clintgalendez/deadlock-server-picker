Closes #

## What and why

## Firewall changes
<!-- If this touches firewall code: paste the relevant `--dry-run` output here. Otherwise delete this section. -->

## Local CI checklist
- [ ] Branch is `<type>/<issue>-<slug>`; PR title is a Conventional Commit written for users
- [ ] Tests added or updated (golden files reviewed if changed)
- [ ] `scripts/ci` run on the **pushed** head; `local-ci` is green (summary is appended below automatically)
- [ ] Docs updated if user-facing
- [ ] No code path touches firewall entries outside our namespace
- [ ] `reset` still leaves zero traces (integration test or reasoning)
