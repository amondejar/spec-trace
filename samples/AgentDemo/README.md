# Demo: SpecTrace + Microsoft Agent Framework

Un agente de [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) que se conecta al servidor
MCP de SpecTrace, consulta un requisito de `samples/Orders` y propone el código y los tests que faltan con las
marcas `[Implements]` / `[Verifies]` correctas.

## Ejecutar

```bash
# 1. Compila la CLI: la demo lanza su servidor MCP desde bin/
dotnet build src/SpecTrace.Tool

# 2. Configura el modelo (cualquier endpoint compatible con la API de OpenAI)
export OPENAI_API_KEY=sk-...                    # OpenAI
# o Claude, con el endpoint compatible de la Claude API (para pruebas, no producción):
export OPENAI_API_KEY=sk-ant-...
export OPENAI_BASE_URL=https://api.anthropic.com/v1/
export OPENAI_MODEL=claude-sonnet-5-5
# o, en local con Ollama:
export OPENAI_BASE_URL=http://localhost:11434/v1
export OPENAI_MODEL=qwen2.5-coder:14b           # un modelo con soporte de herramientas

# 3. Lanza el agente sobre un requisito
dotnet run --project samples/AgentDemo -- ORD-002
dotnet run --project samples/AgentDemo -- ORD-003 --allow-writes
```

| Opción | Efecto |
| --- | --- |
| `<ID>` | Requisito a trabajar (por defecto `ORD-002`). |
| `--allow-writes` | Da al agente también `scaffold_tests` y `propose_requirement`. Sin ella solo lee (`list_requirements`, `get_requirement`, `get_code`, `get_traceability_report`). |
| `--root <dir>` | Carpeta con `spectrace.json` (por defecto `samples/Orders`). |

## Qué esperar

- **ORD-002**: el agente ve que AC-2 (cancelar un pedido enviado) no tiene test y propone uno con
  `[Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_2)]`.
- **ORD-003**: no hay implementación; lee `OrderService.cs` con `get_code`, ve que `Order.Total` es calculado y propone
  `ApplyCoupon` con `[Implements(Requirements.ORD_003)]` encajando en esos tipos, más un test por criterio.
  Con `--allow-writes` además crea los esqueletos en `tests/Orders.Tests/Specs`.

Comprueba el resultado igual que con cualquier cambio: `dotnet build` (avisos ST) y `dotnet spectrace report`.

## Notas

- En Windows, `cmd` usa `set NOMBRE=valor` y PowerShell `$env:NOMBRE="valor"`. Nunca pegues la clave en un chat o un fichero versionado.
- La demo no está en `SpecTrace.sln` y usa versiones flotantes de `Microsoft.Agents.AI` (1.x),
  `Microsoft.Extensions.AI.OpenAI` y `ModelContextProtocol.Core` (2.x). Fíjalas al adoptarla.
- El servidor MCP de SpecTrace implementa el handshake `initialize` (revisiones 2024-11-05 a 2025-11-25).
  Los clientes del SDK 2.x prefieren la revisión 2026-07-28 y vuelven a `initialize` automáticamente.
