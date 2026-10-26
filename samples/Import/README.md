# Import samples

Example specs in the GitHub Spec Kit and OpenSpec formats, to try `dotnet spectrace import`.

```bash
# Spec Kit: writes samples/Import/speckit/specs/001-photo-albums/spectrace.spec.md (prefix PA1)
dotnet spectrace import speckit samples/Import/speckit/specs --root samples/Import/speckit

# OpenSpec: writes samples/Import/openspec/specs/auth.spec.md (prefix AUTH); ignores openspec/changes/
dotnet spectrace import openspec samples/Import/openspec/openspec --root samples/Import/openspec
```

What to look for in the result:

- **Spec Kit**: user stories 1 and 2 are `approved` with their scenarios as criteria; story 3 stays `proposed`
  because it has a `[NEEDS CLARIFICATION]`; FR-001..003 stay `proposed` because Spec Kit gives them no criteria.
- **OpenSpec**: each `### Requirement:` becomes an `AUTH-00n` requirement and each `#### Scenario:` a criterion.
  Add a new requirement at the top of `spec.md` and re-import: existing IDs do not change.
