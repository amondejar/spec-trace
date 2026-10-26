# SpecTrace

Trazabilidad Spec-Driven Development para .NET. Tus specs en Markdown se convierten en un catálogo
tipado de requisitos, y el compilador de C# avisa de lo que está sin implementar o sin test.

```text
specs/orders.spec.md(28,1): warning ST0003: El requisito 'ORD-003' (Aplicar cupón de descuento)
  está approved pero ningún tipo o miembro tiene [Implements("ORD-003")]
```

## Cómo funciona

1. Escribes (o generas con Spec Kit / OpenSpec) specs `*.spec.md` con requisitos identificados:

   ```markdown
   ## ORD-001: Crear pedido
   Status: approved

   When the customer submits a cart with at least one line,
   the system shall create an order in state Pending.

   - AC-1: Given a cart with 2 lines, when it is submitted, then an order with 2 lines is created.
   - AC-2: Given an empty cart, when it is submitted, then the request is rejected.
   ```

2. El generador crea `Requirements.ORD_001` y `AcceptanceCriteria.ORD_001.AC_1`.
3. Marcas código y tests:

   ```csharp
   [Implements(Requirements.ORD_001)]
   public Order Create(IReadOnlyList<OrderLine> cart) { ... }

   [Fact]
   [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)]
   public void Create_with_empty_cart_is_rejected() { ... }
   ```

4. `dotnet build` comprueba la trazabilidad; `dotnet spectrace report` genera la matriz completa.

## Paquetes

| Paquete | Qué contiene |
| --- | --- |
| `SpecTrace` | Analizador + generador Roslyn, code fixes para el IDE y props/targets de MSBuild. Instala `SpecTrace.Abstractions`. |
| `SpecTrace.Abstractions` | Atributos `[Implements]`, `[Verifies]`. |
| `SpecTrace.Tool` | CLI `dotnet spectrace`: `report`, `context`, `scaffold-tests`, `import`, `mcp`, `init`. |

## Empezar

```bash
dotnet new tool-manifest               # herramienta local del repo: se invoca como "dotnet spectrace"
dotnet tool install SpecTrace.Tool --prerelease
dotnet spectrace init                 # spectrace.json, specs/example.spec.md, la skill y .mcp.json
dotnet add src/MiApp package SpecTrace
dotnet add tests/MiApp.Tests package SpecTrace
dotnet build
```

Si prefieres instalarla global (`dotnet tool install -g SpecTrace.Tool --prerelease`), el comando es `spectrace`
en vez de `dotnet spectrace`, también en `.mcp.json`.

Configuración por MSBuild (opcional): `SpecTraceSpecsDir`, `SpecTraceNamespace`, `SpecTraceRole` (`Implementation` | `Tests`).
Severidades con un `.globalconfig` (`is_global = true`), no `.editorconfig`, porque los avisos de cobertura se reportan sobre los `.md`.

## Diagnósticos

| ID | Severidad | Qué significa |
| --- | --- | --- |
| ST0001 | Error | ID de requisito inexistente (code fix: ID parecido) |
| ST0002 | Error | Criterio de aceptación inexistente (code fix: criterio válido) |
| ST0003 | Warning | Requisito aprobado sin implementar |
| ST0004 | Warning | Requisito implementado sin ningún test |
| ST0005 | Info | Criterio de aceptación sin test |
| ST0006 | Warning | Requisito deprecated referenciado (code fix: quitar la referencia) |
| ST0007 | Error | ID duplicado en las specs |
| ST0008 | Warning | Formato de la spec (sin criterios, sin enunciado, estado desconocido) |
| ST0009 | Warning | ID no constante en el atributo |
| ST0010 | Warning | El enunciado cambió después de revisarse: revisa la implementación |
| ST0011 | Warning | Un criterio cambió después de revisarse: revisa sus tests |

Los tests con `Skip` o `[Ignore]` no cuentan como cobertura. Cada regla tiene su página en [`docs/rules`](docs/rules).

## Cambios en la spec (spectrace.lock)

Una spec que cambia después de implementarse es el caso que más se escapa: el código y los tests siguen en verde, pero
ya no cumplen lo que dice el texto. SpecTrace guarda en `spectrace.lock` una huella del enunciado y de cada criterio
en el momento en que se revisaron, y avisa cuando cambian:

```bash
dotnet spectrace lock                          # registra la versión actual de todos los requisitos activos
dotnet spectrace lock --requirement ORD-001    # tras revisar un requisito concreto
dotnet spectrace lock --check                  # en CI: falla si hay cambios sin revisar
```

| Cambia | Aviso | Dónde | Qué revisar |
| --- | --- | --- | --- |
| El enunciado | ST0010 | Proyecto de implementación, en la línea del requisito | Los miembros con `[Implements]` |
| Un criterio | ST0011 | Proyecto de test, en la línea del criterio | Los tests con `[Verifies]` de ese criterio |
| Título, estado o espacios | — | — | Nada: no cambian el comportamiento |
| Se añade un criterio | ST0005 si no tiene test | — | Escribir su test |

Haz commit de `spectrace.lock` junto con la revisión. El fichero tiene una línea por requisito, así que los diffs son
legibles y los conflictos solo afectan al requisito tocado. Registrar una revisión es una decisión humana: la skill y
el servidor MCP piden a los agentes que no ejecuten `lock` por su cuenta.

## En el IDE

Además de los code fixes de la tabla, sobre cualquier `[Implements]` o `[Verifies]` (Ctrl+. en Visual Studio y VS Code, Alt+Enter en Rider):

- **Usar constantes del catálogo de SpecTrace**: `"ORD-001"` → `Requirements.ORD_001`, `"AC-1"` → `AcceptanceCriteria.ORD_001.AC_1`.
- **Generar tests de ORD-001 en MiApp.Tests**: crea `Specs/ORD_001_...Tests.cs` en el proyecto de tests con un test (con `Skip`) por cada criterio que aún no tiene test.

## En las pull requests (GitHub Actions)

```yaml
permissions:
  contents: read
  pull-requests: write

jobs:
  spectrace:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: amondejar/spec-trace@v0.4
        with:
          root: .              # carpeta con spectrace.json
          fail-on-gaps: false  # true para bloquear la PR si hay huecos
          fail-on-drift: true  # bloquear si la spec cambió y nadie revisó el código ni los tests
```

La Action anota los huecos en los ficheros de la PR, añade el informe al resumen del job y mantiene un comentario
con la matriz completa que se actualiza en cada push. Expone las salidas `gaps` (requisitos con huecos) y `drift` (requisitos cuya spec cambió sin revisar).
Para otros sistemas de CI: `dotnet spectrace report --fail-on-gaps` (Markdown) o `--format json`.

## Para agentes de IA

### Skill

`dotnet spectrace init` instala `.claude/skills/spectrace/SKILL.md` (formato Agent Skills; con `--skills-dir`
se coloca donde lo lea tu agente). La skill le dice al agente que parta de la spec, cargue el requisito antes de
implementar y no dé el trabajo por terminado mientras queden diagnósticos ST.

### Servidor MCP

`dotnet spectrace mcp` arranca un servidor [Model Context Protocol](https://modelcontextprotocol.io) por stdio, sin dependencias externas:

| Herramienta | Tipo | Qué hace |
| --- | --- | --- |
| `list_requirements` | lectura | Requisitos con su estado de trazabilidad; `only_gaps` para ver lo que falta y `only_changed` para ver specs cambiadas sin revisar |
| `get_requirement` | lectura | Enunciado, criterios con estado, implementación actual, proyecto y `using` donde va el código, ficheros relacionados y marcas a usar |
| `get_code` | lectura | Código relacionado con un requisito, dónde se declara un tipo o un fichero concreto (solo `.cs`, `.csproj` y `.md` de las carpetas de código y specs) |
| `get_traceability_report` | lectura | Informe completo en Markdown o JSON |
| `scaffold_tests` | escritura | Esqueletos de tests para los criterios sin test (no sobrescribe) |
| `propose_requirement` | escritura | Añade un requisito `proposed` a una spec; una persona lo aprueba después |

Las rutas que recibe quedan confinadas al repositorio y a la carpeta de specs; `get_code` no lee `bin`, `obj`, `.git` ni ficheros de configuración o secretos. Implementa el handshake `initialize`
(revisiones 2024-11-05 a 2025-11-25); los clientes que prefieren la revisión 2026-07-28 vuelven a él automáticamente.

`dotnet spectrace init` crea `.mcp.json` (Claude Code y otros clientes que leen ese fichero). Para VS Code,
`.vscode/mcp.json`:

```json
{
  "servers": {
    "spectrace": { "type": "stdio", "command": "dotnet", "args": ["spectrace", "mcp"] }
  }
}
```

### Demo con Microsoft Agent Framework

[`samples/AgentDemo`](samples/AgentDemo) es un agente de Agent Framework que se conecta a este servidor MCP,
consulta un requisito y propone el código y los tests que faltan. Funciona con cualquier endpoint compatible con
la API de OpenAI (OpenAI, Ollama, LM Studio…).

## Importar desde Spec Kit y OpenSpec

```bash
dotnet spectrace import speckit specs/              # cada specs/NNN-feature/spec.md → spectrace.spec.md al lado
dotnet spectrace import openspec openspec/          # cada openspec/specs/<cap>/spec.md → specs/<cap>.spec.md
```

| Origen | Se convierte en |
| --- | --- |
| Spec Kit: User Story N con sus Acceptance Scenarios | `PA1-US-N` con un criterio `AC-n` por escenario (`approved`) |
| Spec Kit: `**FR-001**` | `PA1-FR-001` en estado `proposed` (Spec Kit no les da criterios propios) |
| Spec Kit: `[NEEDS CLARIFICATION]` | El requisito queda `proposed` con una nota |
| OpenSpec: `### Requirement:` y sus `#### Scenario:` | `AUTH-001` con un criterio por escenario |
| OpenSpec: `openspec/changes/` | No se importa: son propuestas sin archivar |

El prefijo sale de la carpeta de la feature (`001-photo-albums` → `PA1`) o de la capacidad (`auth` → `AUTH`), y se
puede fijar con `--prefix`. Reimportar es seguro: se conservan los IDs (en OpenSpec, por título), los estados que
hayas cambiado y los criterios que añadas a mano a requisitos sin criterios. `--dry-run` muestra qué haría.
Ejemplos en [`samples/Import`](samples/Import).

## Estructura

```text
src/SpecTrace.Abstractions   atributos (netstandard2.0)
src/SpecTrace.Analyzers      parser de specs, generador, analizador, build/*.props|targets (netstandard2.0)
src/SpecTrace.CodeFixes      code fixes y refactorizaciones para el IDE (netstandard2.0)
src/SpecTrace.Package        empaqueta analizador + code fixes como el paquete "SpecTrace"
src/SpecTrace.Tool           CLI dotnet spectrace y servidor MCP (net8.0, RollForward Major)
tests/SpecTrace.Tests        tests del parser, generador, analizador, code fixes, MCP e importadores
docs/rules                   una página por diagnóstico
action.yml                   GitHub Action para PRs
samples/Orders               ejemplo completo con huecos intencionados (ORD-003 sin implementar, ORD-002 AC-2 sin test)
                             y un cambio sin revisar (ORD-001 AC-2 cambió después del spectrace.lock)
samples/Import               specs de ejemplo de Spec Kit y OpenSpec para probar la importación
samples/AgentDemo            agente de Microsoft Agent Framework que usa el servidor MCP
```

## Desarrollo

```bash
dotnet test tests/SpecTrace.Tests
dotnet build samples/Orders/tests/Orders.Tests     # muestra ST0003, ST0005 y ST0011
dotnet run --project src/SpecTrace.Tool -- report --root samples/Orders
dotnet pack src/SpecTrace.Abstractions -c Release -o artifacts
dotnet pack src/SpecTrace.Package -c Release -o artifacts     # paquete "SpecTrace"
dotnet pack src/SpecTrace.Tool -c Release -o artifacts
```
