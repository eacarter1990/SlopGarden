using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Ai.Tools;

/// <summary>Reflection helpers shared by tools: patch semantics (null = unchanged) and field-level diffs for approval prompts.</summary>
public static class Patch
{
    private static readonly string[] Ignored = ["reason"];

    private static IEnumerable<(PropertyInfo Prop, object Value)> SetProps(object input) =>
        input.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !Ignored.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(p => (p, p.GetValue(input)))
            .Where(x => x.Item2 is not null)
            .Select(x => (x.p, x.Item2!));

    /// <summary>Copies every non-null input property onto the same-named entity property. Returns the names changed.</summary>
    public static List<string> Apply(object entity, object input, params string[] skip)
    {
        var changed = new List<string>();
        var et = entity.GetType();
        foreach (var (prop, value) in SetProps(input))
        {
            if (skip.Contains(prop.Name)) continue;
            var target = et.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
            if (target is null || !target.CanWrite) continue;
            var targetType = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
            object converted = value;
            if (value is JsonNode node) converted = node.DeepClone();
            else if (!targetType.IsInstanceOfType(value)) converted = Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
            if (Equals(target.GetValue(entity), converted)) continue;
            target.SetValue(entity, converted);
            changed.Add(prop.Name);
        }
        return changed;
    }

    public static JsonArray CreateChanges(object input, params string[] skip)
    {
        var arr = new JsonArray();
        foreach (var (prop, value) in SetProps(input))
        {
            if (skip.Contains(prop.Name)) continue;
            arr.Add(Change(prop.Name, null, value));
        }
        return arr;
    }

    public static JsonArray UpdateChanges(object entity, object input, params string[] skip)
    {
        var arr = new JsonArray();
        var et = entity.GetType();
        foreach (var (prop, value) in SetProps(input))
        {
            if (skip.Contains(prop.Name)) continue;
            var before = et.GetProperty(prop.Name)?.GetValue(entity);
            if (Json(before) == Json(value)) continue;
            arr.Add(Change(prop.Name, before, value));
        }
        return arr;
    }

    public static JsonObject Change(string field, object? before, object? after) => new()
    {
        ["field"] = JsonNamingPolicy.CamelCase.ConvertName(field),
        ["before"] = JsonSerializer.SerializeToNode(before, ToolRegistry.Json),
        ["after"] = JsonSerializer.SerializeToNode(after, ToolRegistry.Json),
    };

    private static string Json(object? v) => JsonSerializer.Serialize(v, ToolRegistry.Json);
}

public static class Ensure
{
    public static string? OneOf(string? value, string[] allowed, string field)
    {
        if (value is null) return null;
        if (!allowed.Contains(value)) throw AppException.Bad($"{field} must be one of: {string.Join(", ", allowed)}");
        return value;
    }

    public static T Found<T>(T? value, string what) where T : class => value ?? throw AppException.NotFound(what);

    public static void Write(ToolContext ctx)
    {
        if (!ctx.Actor.CanWrite && ctx.Mode != ToolMode.Approved) throw AppException.Forbidden();
    }
}
