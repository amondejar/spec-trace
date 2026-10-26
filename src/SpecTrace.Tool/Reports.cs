using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SpecTrace.Specs;

namespace SpecTrace.Tool;

public static class Reports
{
    /// <param name="linkBase">
    /// Prefijo para convertir rutas en enlaces absolutos (p. ej. https://github.com/org/repo/blob/&lt;sha&gt;/samples/Orders/),
    /// necesario cuando el informe se publica como comentario de una PR.
    /// </param>
    public static string Markdown(TraceModel model, string? linkBase = null)
    {
        string Href(string file, int line) => $"{linkBase}{file}#L{line}";
        string Link(TraceLink l) => linkBase == null
            ? $"`{l.Symbol}` ({l.File}:{l.Line})"
            : $"[`{l.Symbol}`]({Href(l.File, l.Line)})";

        var sb = new StringBuilder();
        var active = model.Requirements.Where(r => r.Requirement.IsEnforced).ToList();
        var implemented = active.Count(r => r.IsImplemented);
        var verified = active.Count(r => r.IsVerified);
        var criteria = active.Sum(r => r.Requirement.Criteria.Count);
        var covered = active.Sum(r => r.CoveredCriteria);

        sb.AppendLine("# Informe de trazabilidad");
        sb.AppendLine();
        sb.AppendLine($"Generado el {DateTime.Now:yyyy-MM-dd HH:mm} por SpecTrace.");
        sb.AppendLine();
        sb.AppendLine($"- Requisitos activos: **{active.Count}** (approved o implemented)");
        sb.AppendLine($"- Implementados: **{implemented}/{active.Count}**");
        sb.AppendLine($"- Con al menos un test: **{verified}/{active.Count}**");
        sb.AppendLine($"- Criterios de aceptación con test: **{covered}/{criteria}**");
        var drifted = model.Requirements.Where(t => t.HasDrift).ToList();
        sb.AppendLine(model.HasLock
            ? $"- Cambios en la spec sin revisar: **{drifted.Count}**"
            : "- Sin `spectrace.lock`: ejecuta `dotnet spectrace lock` para detectar cambios en la spec");
        sb.AppendLine();
        sb.AppendLine("| Requisito | Estado | Implementado en | Verificado por | Criterios |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var t in model.Requirements)
        {
            var r = t.Requirement;
            var impl = t.IsImplemented ? string.Join("<br>", t.Implementations.Select(Link)) : r.IsEnforced ? "**falta**" : "–";
            var tests = t.IsVerified ? string.Join("<br>", t.Verifications.Select(v => Link(v) + (v.Criterion != null ? $" ({v.Criterion})" : ""))) : r.IsEnforced ? "**falta**" : "–";
            var crit = r.Criteria.Count == 0 ? "–" : $"{t.CoveredCriteria}/{r.Criteria.Count}";
            sb.AppendLine($"| [{r.Id}]({Href(r.FilePath, r.Line + 1)}) {Escape(r.Title)} | {r.Status} | {impl} | {tests} | {crit} |");
        }

        var gaps = model.Requirements.Where(t => t.HasGap).ToList();
        if (gaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Huecos");
            sb.AppendLine();
            foreach (var t in gaps)
            {
                var r = t.Requirement;
                if (!t.IsImplemented) sb.AppendLine($"- {r.Id} {r.Title}: sin implementación");
                if (!t.IsVerified) sb.AppendLine($"- {r.Id} {r.Title}: sin tests");
                else foreach (var c in t.UncoveredCriteria) sb.AppendLine($"- {r.Id} {c.Id}: sin test ({c.Text})");
            }
        }

        if (drifted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Cambios en la spec desde la última revisión");
            sb.AppendLine();
            sb.AppendLine("El texto cambió después de `dotnet spectrace lock`. Revisa lo indicado y, cuando esté al día, ejecuta `dotnet spectrace lock --requirement <ID>`.");
            sb.AppendLine();
            foreach (var t in drifted)
            {
                var r = t.Requirement;
                if (t.CodeNeedsReview)
                {
                    sb.AppendLine($"- [{r.Id}]({Href(r.FilePath, r.Line + 1)}) enunciado → revisa {string.Join(", ", t.Implementations.Select(Link))}");
                }

                foreach (var c in t.CriteriaNeedingReview)
                {
                    var tests = t.Verifications.Where(v => v.Criterion == c.Id).Select(Link);
                    sb.AppendLine($"- [{r.Id} {c.Id}]({Href(r.FilePath, c.Line + 1)}) \"{Escape(c.Text)}\" → revisa {string.Join(", ", tests)}");
                }
            }
        }

        var unlocked = model.HasLock
            ? model.Requirements.Where(t => t.Requirement.IsEnforced && !t.DriftInfo.IsLocked && (t.IsImplemented || t.IsVerified)).ToList()
            : new List<RequirementTrace>();
        if (unlocked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Sin revisión registrada en `spectrace.lock`: {string.Join(", ", unlocked.Select(t => t.Requirement.Id))}.");
        }

        if (model.UnknownLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Referencias a requisitos que no existen");
            sb.AppendLine();
            foreach (var l in model.UnknownLinks) sb.AppendLine($"- `{l.RequirementId}` en {Link(l)}");
        }

        if (model.Catalog.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Problemas en las specs");
            sb.AppendLine();
            foreach (var i in model.Catalog.Issues) sb.AppendLine($"- {i.FilePath}:{i.Line + 1} {i.Message}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Comandos de flujo de trabajo de GitHub Actions (<c>::warning file=...,line=...::mensaje</c>):
    /// GitHub los muestra como anotaciones en la pestaña "Files changed" de la PR.
    /// </summary>
    /// <param name="baseDir">Raíz del repositorio (GITHUB_WORKSPACE); las rutas se expresan relativas a ella.</param>
    public static string GitHub(TraceModel model, string baseDir)
    {
        var sb = new StringBuilder();
        string Path(string relative) => TraceScanner.Relative(baseDir, System.IO.Path.Combine(model.Root, relative));

        void Emit(string level, string file, int line, string code, string title, string message) =>
            sb.Append("::").Append(level)
              .Append(" file=").Append(Property(Path(file)))
              .Append(",line=").Append(line)
              .Append(",title=").Append(Property($"{code} {title}"))
              .Append("::").AppendLine(Data(message));

        foreach (var t in model.Requirements.Where(t => t.Requirement.IsEnforced))
        {
            var r = t.Requirement;
            if (!t.IsImplemented)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0003", "Requisito sin implementar",
                    $"{r.Id} ({r.Title}) está {r.Status} pero nada tiene [Implements(\"{r.Id}\")]");
            }
            else if (!t.IsVerified)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0004", "Requisito sin test",
                    $"{r.Id} ({r.Title}) está implementado pero ningún test tiene [Verifies(\"{r.Id}\")]");
            }
            else
            {
                foreach (var c in t.UncoveredCriteria)
                {
                    Emit("notice", r.FilePath, c.Line + 1, "ST0005", "Criterio sin test", $"{r.Id} {c.Id}: {c.Text}");
                }
            }
        }

        foreach (var t in model.Requirements.Where(t => t.HasDrift))
        {
            var r = t.Requirement;
            if (t.CodeNeedsReview)
            {
                Emit("warning", r.FilePath, r.Line + 1, "ST0010", "Enunciado cambiado",
                    $"El enunciado de {r.Id} cambió después de la última revisión. Revisa {string.Join(", ", t.Implementations.Select(l => l.Symbol).Distinct())} y ejecuta: dotnet spectrace lock --requirement {r.Id}");
            }

            foreach (var c in t.CriteriaNeedingReview)
            {
                var tests = t.Verifications.Where(v => v.Criterion == c.Id).Select(v => v.Symbol).Distinct();
                Emit("warning", r.FilePath, c.Line + 1, "ST0011", "Criterio cambiado",
                    $"El criterio {c.Id} de {r.Id} cambió después de la última revisión. Revisa {string.Join(", ", tests)} y ejecuta: dotnet spectrace lock --requirement {r.Id}");
            }
        }

        foreach (var l in model.UnknownLinks)
        {
            Emit("error", l.File, l.Line, "ST0001", "Requisito desconocido", $"'{l.RequirementId}' no existe en ninguna spec");
        }

        foreach (var i in model.Catalog.Issues)
        {
            var dup = i.Kind is SpecIssueKind.DuplicateRequirement or SpecIssueKind.DuplicateCriterion;
            Emit(dup ? "error" : "warning", i.FilePath, i.Line + 1, dup ? "ST0007" : "ST0008",
                dup ? "ID duplicado" : "Formato de la spec", i.Message);
        }

        return sb.ToString();
    }

    // https://docs.github.com/actions/using-workflows/workflow-commands-for-github-actions
    private static string Data(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string Property(string s) => Data(s).Replace(":", "%3A").Replace(",", "%2C");

    public static string Json(TraceModel model)
    {
        var payload = new
        {
            generatedAt = DateTimeOffset.Now,
            hasLock = model.HasLock,
            requirements = model.Requirements.Select(t => new
            {
                id = t.Requirement.Id,
                title = t.Requirement.Title,
                status = t.Requirement.Status,
                statement = t.Requirement.Statement,
                spec = $"{t.Requirement.FilePath}:{t.Requirement.Line + 1}",
                implementedBy = t.Implementations.Select(l => new { symbol = l.Symbol, location = $"{l.File}:{l.Line}" }),
                verifiedBy = t.Verifications.Select(l => new { symbol = l.Symbol, criterion = l.Criterion, location = $"{l.File}:{l.Line}" }),
                criteria = t.Requirement.Criteria.Select(c => new
                {
                    id = c.Id,
                    text = c.Text,
                    verified = t.Verifications.Any(v => v.Criterion == c.Id),
                }),
                gap = t.HasGap,
                drift = new
                {
                    locked = t.DriftInfo.IsLocked,
                    statementChanged = t.DriftInfo.StatementChanged,
                    criteriaChanged = t.DriftInfo.ChangedCriteria.Select(c => c.Id),
                    pending = t.HasDrift,
                },
            }),
            unknownReferences = model.UnknownLinks.Select(l => new { id = l.RequirementId, location = $"{l.File}:{l.Line}" }),
            specIssues = model.Catalog.Issues.Select(i => new { file = i.FilePath, line = i.Line + 1, kind = i.Kind.ToString(), message = i.Message }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Contexto de un requisito pensado para un agente de código: qué hay que construir, qué existe ya,
    /// qué falta y cómo dejar las marcas.
    /// </summary>
    public static string Context(TraceModel model, string id)
    {
        var t = model.Requirements.FirstOrDefault(r => string.Equals(r.Requirement.Id, id, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            var known = string.Join(", ", model.Requirements.Select(r => r.Requirement.Id));
            throw new ArgumentException($"No existe el requisito '{id}'. Requisitos disponibles: {known}");
        }

        var r = t.Requirement;
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Id}: {r.Title}");
        sb.AppendLine();
        sb.AppendLine($"Estado: {r.Status} · Spec: {r.FilePath}:{r.Line + 1}");
        sb.AppendLine();
        if (t.HasDrift || (t.DriftInfo.Any && t.Requirement.IsEnforced))
        {
            sb.AppendLine("## Cambios desde la última revisión");
            sb.AppendLine();
            if (t.DriftInfo.StatementChanged)
            {
                sb.AppendLine(t.IsImplemented
                    ? $"- El enunciado cambió: revisa que {string.Join(", ", t.Implementations.Select(Link))} sigue cumpliéndolo."
                    : "- El enunciado cambió (aún no hay implementación).");
            }

            foreach (var c in t.DriftInfo.ChangedCriteria)
            {
                var tests = t.Verifications.Where(v => v.Criterion == c.Id).ToList();
                sb.AppendLine(tests.Count > 0
                    ? $"- {c.Id} cambió: revisa {string.Join(", ", tests.Select(Link))}."
                    : $"- {c.Id} cambió (aún no tiene test).");
            }

            sb.AppendLine("- Cuando una persona haya revisado el código y los tests, se registra con `dotnet spectrace lock --requirement " + r.Id + "`. No lo ejecutes tú sin su visto bueno.");
            sb.AppendLine();
        }

        sb.AppendLine("## Enunciado");
        sb.AppendLine();
        sb.AppendLine(r.Statement.Length > 0 ? r.Statement : "(sin enunciado)");
        sb.AppendLine();
        sb.AppendLine("## Criterios de aceptación");
        sb.AppendLine();
        foreach (var c in r.Criteria)
        {
            var tests = t.Verifications.Where(v => v.Criterion == c.Id).ToList();
            var state = tests.Count > 0 ? "con test: " + string.Join(", ", tests.Select(Link)) : "SIN TEST";
            sb.AppendLine($"- {c.Id}: {c.Text} — {state}");
        }

        sb.AppendLine();
        sb.AppendLine("## Implementación actual");
        sb.AppendLine();
        if (t.IsImplemented) foreach (var l in t.Implementations) sb.AppendLine($"- {Link(l)}");
        else sb.AppendLine("- Ninguna todavía.");
        sb.AppendLine();
        AppendCodeContext(sb, model, t);
        sb.AppendLine("## Cómo marcar el trabajo");
        sb.AppendLine();
        sb.AppendLine($"- En el código que lo implementa: `[Implements(Requirements.{r.Identifier})]`");
        foreach (var c in r.Criteria)
        {
            sb.AppendLine($"- En el test de {c.Id}: `[Verifies(Requirements.{r.Identifier}, AcceptanceCriteria.{r.Identifier}.{SpecNaming.ToIdentifier(c.Id)})]`");
        }

        sb.AppendLine("- Comprueba con `dotnet build`: no deben quedar ST0003/ST0004/ST0005 para este requisito.");
        return sb.ToString();
    }

    /// <summary>Ficheros que ya trabajan con requisitos de la misma spec, con los IDs que implementan o verifican.</summary>
    public static IReadOnlyList<(string File, LinkKind Kind, IReadOnlyList<string> Ids)> RelatedFiles(TraceModel model, RequirementTrace t)
    {
        var sameSpec = model.Requirements.Where(x => x.Requirement.FilePath == t.Requirement.FilePath).ToList();
        return sameSpec
            .SelectMany(x => x.Implementations.Concat(x.Verifications))
            .GroupBy(l => (l.File, l.Kind))
            .Select(g => (g.Key.File, g.Key.Kind, (IReadOnlyList<string>)g.Select(l => l.RequirementId).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList()))
            // Primero los ficheros de este requisito; luego implementación antes que tests.
            .OrderByDescending(f => f.Item3.Contains(t.Requirement.Id))
            .ThenBy(f => f.Kind)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Dónde va el código y qué leer antes de escribirlo: proyectos con su namespace de catálogo y ficheros relacionados.
    /// Sin esto, un agente que no puede explorar el repositorio inventa tipos y namespaces.
    /// </summary>
    private static void AppendCodeContext(StringBuilder sb, TraceModel model, RequirementTrace t)
    {
        var related = RelatedFiles(model, t);
        sb.AppendLine("## Dónde va el código");
        sb.AppendLine();

        IEnumerable<ProjectInfo> ProjectsFor(LinkKind kind, bool isTest)
        {
            var used = related.Where(f => f.Kind == kind).Select(f => model.ProjectOf(f.File)).Where(p => p != null && p.IsTest == isTest).Distinct().ToList();
            return used.Count > 0 ? used! : model.Projects.Where(p => p.IsTest == isTest);
        }

        var impl = ProjectsFor(LinkKind.Implements, isTest: false).ToList();
        var tests = ProjectsFor(LinkKind.Verifies, isTest: true).ToList();
        if (impl.Count == 0 && tests.Count == 0)
        {
            sb.AppendLine("- No se ha encontrado ningún .csproj en las carpetas de código.");
        }

        foreach (var p in impl) sb.AppendLine($"- Implementación en `{p.Name}` ({p.Directory}): `using SpecTrace;` y `using {p.CatalogNamespace};`");
        foreach (var p in tests) sb.AppendLine($"- Tests en `{p.Name}` ({p.Directory}): `using SpecTrace;` y `using {p.CatalogNamespace};`");
        sb.AppendLine("- `Requirements` y `AcceptanceCriteria` son clases generadas por proyecto (internal): usa el namespace de cada proyecto.");
        sb.AppendLine();

        sb.AppendLine("## Código relacionado");
        sb.AppendLine();
        if (related.Count == 0)
        {
            sb.AppendLine("- Ningún fichero trabaja aún con requisitos de esta spec. Busca los tipos del dominio con get_code (type).");
        }
        else
        {
            sb.AppendLine("Ficheros que ya trabajan con requisitos de esta spec. Léelos (get_code) antes de proponer cambios y reutiliza sus tipos:");
            sb.AppendLine();
            foreach (var (file, kind, ids) in related)
            {
                sb.AppendLine($"- {file} — {(kind == LinkKind.Implements ? "implementa" : "verifica")} {string.Join(", ", ids)}");
            }
        }

        sb.AppendLine();
    }

    private static string Link(TraceLink l) => $"`{l.Symbol}` ({l.File}:{l.Line})";

    private static string Escape(string text) => text.Replace("|", "\\|");
}
