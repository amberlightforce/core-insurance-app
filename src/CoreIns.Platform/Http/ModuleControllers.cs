using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.Http;

/// <summary>
/// Registers module assemblies as MVC application parts and lets their <b>internal</b> <c>[ApiController]</c> classes be
/// controllers, so a module exposes REST endpoints without making its command, query or persistence types public.
/// </summary>
public static class ModuleControllers
{
    /// <summary>Adds the module assemblies and the internal-controller convention.</summary>
    public static IMvcBuilder AddModuleControllers(this IMvcBuilder builder, IEnumerable<Assembly> moduleAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(moduleAssemblies);
        var assemblies = moduleAssemblies.ToHashSet();
        foreach (var assembly in assemblies)
        {
            builder.AddApplicationPart(assembly);
        }

        builder.ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new InternalControllerFeatureProvider(assemblies)));
        return builder;
    }

    private sealed class InternalControllerFeatureProvider(IReadOnlySet<Assembly> assemblies) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            assemblies.Contains(typeInfo.Assembly)
            && typeInfo is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
            && typeof(ControllerBase).IsAssignableFrom(typeInfo)
            && typeInfo.IsDefined(typeof(ApiControllerAttribute), inherit: true);
    }
}
