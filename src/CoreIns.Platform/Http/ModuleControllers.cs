using System.Reflection;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
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

        // A body that cannot be read (malformed JSON, wrong types) becomes <MOD>-ERR-VALIDATION Problem Details like
        // every other refusal, instead of MVC's default validation response.
        builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = context =>
        {
            var error = new DomainError(ErrorCode.For(ApiRoutes.ModuleOf(context.HttpContext.Request.Path), PlatformErrors.Validation), "The request could not be read.")
            {
                FieldErrors = [.. context.ModelState
                    .Where(entry => entry.Value is { Errors.Count: > 0 })
                    .Select(entry => new FieldError(entry.Key, "UNREADABLE", "request.unreadable"))],
            };
            var problem = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().Create(error, context.HttpContext);
            return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        });
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
