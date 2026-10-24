using System.Reflection;
using System.Runtime.Loader;

namespace SlopGarden.Client.Plugins;

/// <summary>Isolated (non-collectible) load context for one external plugin. Contract assemblies are always
/// resolved from the host so plugin types implement the very same interfaces the client uses.</summary>
internal sealed class PluginLoadContext(string entryAssemblyPath)
    : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    public static bool IsShared(AssemblyName name) =>
        name.Name is { } n && (
            n is "SlopGarden.Plugins.Abstractions" or "SlopGarden.Contracts"
            || n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName)) return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
