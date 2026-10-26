# Muestras de importación

Specs de ejemplo en los formatos de GitHub Spec Kit y OpenSpec, para probar `dotnet spectrace import`.

```bash
# Spec Kit: genera samples/Import/speckit/specs/001-photo-albums/spectrace.spec.md (prefijo PA1)
dotnet spectrace import speckit samples/Import/speckit/specs --root samples/Import/speckit

# OpenSpec: genera samples/Import/openspec/specs/auth.spec.md (prefijo AUTH); ignora openspec/changes/
dotnet spectrace import openspec samples/Import/openspec/openspec --root samples/Import/openspec
```

Qué mirar en el resultado:

- **Spec Kit**: las user stories 1 y 2 quedan `approved` con sus escenarios como criterios; la 3 queda `proposed`
  porque tiene un `[NEEDS CLARIFICATION]`; los FR-001..003 quedan `proposed` porque Spec Kit no les da criterios.
- **OpenSpec**: cada `### Requirement:` es un requisito `AUTH-00n` y cada `#### Scenario:` un criterio.
  Añade un requisito nuevo al principio del `spec.md` y reimporta: los IDs existentes no cambian.
