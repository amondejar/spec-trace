---
name: spectrace
description: Implementa o modifica funcionalidad de este repositorio partiendo de las specs en specs/*.spec.md, dejando trazabilidad con [Implements] y [Verifies]. Úsala siempre que la tarea mencione un requisito (ORD-001, PAY-API-12...), una spec o un criterio de aceptación, o cuando vayas a añadir comportamiento nuevo.
---

# Trabajar con SpecTrace

Este repositorio sigue Spec-Driven Development. Las specs en `specs/*.spec.md` son la fuente de verdad;
el código y los tests declaran qué requisito implementan y qué criterio verifican, y el compilador
lo comprueba (diagnósticos `ST0001`–`ST0009`).

## Antes de escribir código

1. Localiza el requisito. Si la tarea no da un ID, busca en `specs/` el que corresponde.
   Si no existe ninguno, **no inventes comportamiento**: propone primero el requisito en la spec
   (ID nuevo, enunciado EARS, criterios `AC-n`, `Status: proposed`) y pide confirmación.
2. Carga su contexto. Si tienes el servidor MCP `spectrace` conectado, usa sus herramientas:

   | Herramienta | Para qué |
   | --- | --- |
   | `get_requirement` | Enunciado, criterios con su estado, dónde va el código (proyecto y `using`) y las marcas exactas |
   | `get_code` | Leer el código relacionado (`requirement_id`), dónde se declara un tipo (`type`) o un fichero (`path`) |
   | `list_requirements` | Qué requisitos hay; con `only_gaps: true`, solo lo que falta |
   | `propose_requirement` | Añadir a la spec un requisito nuevo en estado `proposed` |
   | `scaffold_tests` | Crear esqueletos de tests para los criterios sin test |

   Sin MCP, el equivalente en la terminal es:

   ```bash
   dotnet spectrace context <ID>
   ```

## Al implementar

- Lee antes el código relacionado y reutiliza sus tipos: no supongas propiedades, setters ni constructores.
- Marca el tipo o miembro que implementa el requisito con `[Implements(Requirements.<ID_CON_GUIONES_BAJOS>)]`.
  Usa las constantes generadas (`Requirements`, `AcceptanceCriteria`), nunca cadenas sueltas.
- Escribe un test por criterio de aceptación con
  `[Verifies(Requirements.<ID>, AcceptanceCriteria.<ID>.AC_n)]`. El nombre del test describe el criterio.
- Si necesitas esqueletos: `dotnet spectrace scaffold-tests --project <carpeta del proyecto de tests> --requirement <ID>`.
  Los tests generados llevan `Skip`; quítalo al implementarlos (un test saltado no cuenta como cobertura).
- No cambies el texto de un requisito `approved` para que encaje con tu código. Si la spec es ambigua
  o incorrecta, dilo y propone el cambio de la spec por separado.

## Si la spec cambió (ST0010 / ST0011)

Si el build avisa con `ST0010` o `ST0011`, o `list_requirements` con `only_changed: true` devuelve el requisito,
su texto cambió después de la última revisión. Revisa el código o los tests que indica el aviso contra el texto
nuevo y adáptalos. **No ejecutes `dotnet spectrace lock`**: registrar la revisión le corresponde a una persona.
Dile qué has revisado y qué has cambiado.

## Para terminar

1. `dotnet build`: no debe quedar ningún `ST0001`, `ST0002`, `ST0003`, `ST0004` ni `ST0005` del requisito.
2. `dotnet test`: todos los tests del requisito en verde.
3. `dotnet spectrace report` (o `list_requirements` con `only_gaps: true`): el requisito ya no aparece como hueco.
4. En el resumen del cambio, enumera los requisitos y criterios cubiertos por ID.

## Referencia rápida de diagnósticos

| ID | Qué significa | Qué hacer |
| --- | --- | --- |
| ST0001 | ID de requisito inexistente | Corrige el ID o añade el requisito a la spec |
| ST0002 | Criterio inexistente | Usa uno de los `AC-n` del requisito |
| ST0003 | Requisito aprobado sin implementar | Implementa y marca con `[Implements]` |
| ST0004 | Implementado sin ningún test | Añade tests con `[Verifies]` |
| ST0005 | Criterio sin test | Añade el test de ese criterio |
| ST0006 | Requisito deprecated referenciado | Retira la referencia |
| ST0010 | El enunciado cambió tras la última revisión | Revisa la implementación y avisa a una persona |
| ST0011 | Un criterio cambió tras la última revisión | Revisa sus tests y avisa a una persona |
