# Changelog

## 0.4.0-preview.1

### Nuevo

- **Detección de cambios en la spec.** `spectrace.lock` guarda una huella del enunciado y de cada criterio de cada
  requisito revisado. Si el texto cambia después:
  - **ST0010** (proyecto de implementación): el enunciado cambió; nombra los miembros con `[Implements]` a revisar.
  - **ST0011** (proyecto de test): un criterio cambió; nombra los tests con `[Verifies]` a revisar.
  Título, estado y espacios no cuentan. Un criterio nuevo no es un cambio (si no tiene test, ST0005).
- `dotnet spectrace lock` (todos los activos), `lock --requirement <ID>` (repetible) y `lock --check` (para CI).
- Informe: resumen y sección "Cambios en la spec desde la última revisión"; JSON con `drift` por requisito;
  anotaciones ST0010/ST0011 en `--format github`; `report --fail-on-drift`.
- GitHub Action: salida `drift` e input `fail-on-drift`.
- MCP: `list_requirements` con `only_changed` y campo `spec_changed`; `get_requirement` explica qué cambió y pide
  no registrar la revisión sin visto bueno humano.
- La muestra incluye un `spectrace.lock` con un cambio sin revisar (ORD-001 AC-2) para ver ST0011.
- Documentación de ST0010 y ST0011.

## 0.3.1-preview.1

Lo que faltaba para que un agente sin acceso propio al repositorio (como la demo de Agent Framework) proponga código
que compile: la primera prueba real supuso tipos y namespaces que no existían.

### Nuevo

- Herramienta MCP **`get_code`** (solo lectura): código relacionado con un requisito (o, si aún no tiene, el de otros
  requisitos de la misma spec), los ficheros donde se declara un tipo, o un fichero concreto. Solo `.cs`, `.csproj` y `.md`
  de las carpetas de código y specs; nunca `bin`, `obj`, `.git` ni configuración.
- `get_requirement` y `dotnet spectrace context` indican **dónde va el código**: cada proyecto con el `using` de su
  catálogo generado (`Orders.Specs`, `Orders.Tests.Specs`), y los **ficheros relacionados** que conviene leer.
- La demo de Agent Framework usa `get_code` y sus instrucciones piden no suponer tipos. Documentado el uso con Claude.

### Corregido

- El servidor MCP lee y escribe en UTF-8 sin depender de la página de códigos de la consola: en Windows, las tildes que
  enviaba el cliente (por ejemplo en `propose_requirement`) podían llegar estropeadas.
- La salida JSON (MCP y `report --format json`) ya no escapa las tildes (`\u00F3` → `ó`).
- Test de reimportación independiente del salto de línea (fallaba en Windows) y seguimiento de versiones de reglas (RS2008).
- Documentación: instalar `SpecTrace.Tool` como herramienta local para invocarla como `dotnet spectrace`.

## 0.3.0-preview.1

### Nuevo

- **Servidor MCP** (`dotnet spectrace mcp`): stdio, JSON-RPC 2.0, sin dependencias. Herramientas `list_requirements`,
  `get_requirement`, `get_traceability_report` (lectura) y `scaffold_tests`, `propose_requirement` (escritura, con rutas
  confinadas al repositorio). Revisiones 2024-11-05 a 2025-11-25; responde "method not found" a `server/discover`
  para que los clientes de la revisión 2026-07-28 vuelvan a `initialize`.
- **Importación** desde GitHub Spec Kit (`import speckit`) y OpenSpec (`import openspec`), reimportable: conserva IDs,
  estados y criterios añadidos a mano.
- **Demo con Microsoft Agent Framework** en `samples/AgentDemo` (fuera de la solución).
- `init` crea también `.mcp.json`. La skill explica las herramientas MCP.
- Muestras de importación en `samples/Import`.

### Cambios

- El parser ignora las citas (`> ...`) al leer el enunciado: sirven para notas.
- `SpecTrace.Tool` referencia `SpecTrace.Analyzers` en vez de compilar sus ficheros, para que los tests puedan usarlo.

## 0.2.0-preview.1

### Nuevo

- **Code fixes en el IDE** (nuevo ensamblado `SpecTrace.CodeFixes`, incluido en el paquete `SpecTrace`):
  - ST0001: *Cambiar a 'ORD-001'*, con hasta tres IDs existentes parecidos.
  - ST0002: *Cambiar a 'AC-n'*, una opción por criterio válido del requisito.
  - ST0006: *Quitar la referencia al requisito obsoleto*, conservando comentarios XML.
- **Refactorizaciones** sobre `[Implements]` / `[Verifies]`:
  - *Usar constantes del catálogo de SpecTrace*: `"ORD-001"` → `Requirements.ORD_001` y `"AC-1"` → `AcceptanceCriteria.ORD_001.AC_1`, añadiendo el `using`.
  - *Generar tests de ORD-001 en X.Tests*: crea `Specs/ORD_001_...Tests.cs` en cada proyecto de test que referencia el proyecto, solo con los criterios que aún no tienen test.
- **GitHub Action** (`action.yml` en la raíz): anotaciones en los ficheros de la PR, resumen del job, comentario con la matriz que se actualiza en cada push, salida `gaps` y opción `fail-on-gaps`.
- CLI: `report --format github` (anotaciones de GitHub Actions) y `report --link-base <url>` (enlaces absolutos en el Markdown).
- Documentación de cada regla en `docs/rules/ST000x.md` (los enlaces de ayuda de los diagnósticos apuntan ahí).
- Workflow de CI del repositorio: build, tests, pack y la Action ejecutada contra la muestra.

### Cambios

- El paquete `SpecTrace` se construye ahora desde `src/SpecTrace.Package` (analizador + code fixes). `SpecTrace.Analyzers` ya no se empaqueta por sí solo: usa `dotnet pack src/SpecTrace.Package`.
- ST0001 y ST0002 incluyen sugerencias en `Diagnostic.Properties` (`SpecTrace.Suggestions`) para herramientas externas.
- La plantilla de tests se comparte entre la CLI y el IDE (`TestSkeleton`). Los ficheros generados ya no repiten el `using` de su propio namespace.

## 0.1.0-preview.1

- Primera versión: parser de specs Markdown con requisitos identificados, catálogo tipado generado, analizador con las reglas ST0001–ST0009, CLI `dotnet spectrace` (`report`, `context`, `scaffold-tests`, `init`), skill para agentes y muestra `samples/Orders`.
