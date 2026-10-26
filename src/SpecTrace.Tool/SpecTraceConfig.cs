using System;
using System.IO;
using System.Text.Json;

namespace SpecTrace.Tool;

/// <summary>Contenido de spectrace.json, que marca la raíz del repo para SpecTrace.</summary>
public sealed class SpecTraceConfig
{
    public const string FileName = "spectrace.json";

    public string Root { get; set; } = ".";
    public string Specs { get; set; } = "specs";
    public string[] Sources { get; set; } = { "src", "tests" };

    /// <summary>Busca spectrace.json desde <paramref name="start"/> hacia arriba. Sin fichero: valores por defecto.</summary>
    public static SpecTraceConfig Load(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            var file = Path.Combine(dir.FullName, FileName);
            if (File.Exists(file))
            {
                var config = JsonSerializer.Deserialize<SpecTraceConfig>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                    ?? new SpecTraceConfig();
                config.Root = dir.FullName;
                return config;
            }

            dir = dir.Parent;
        }

        return new SpecTraceConfig { Root = Path.GetFullPath(start) };
    }
}
