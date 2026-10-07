using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Platform.Commands;

/// <summary>
/// Explicit wiring of the command pipeline (no MediatR, no assembly scanning). The resolved
/// <see cref="ICommandHandler{TCommand,TResult}"/> is, from the outside in:
/// validation → transaction (+ outbox and audit flush on commit) → idempotency → audit → authority → the handler.
/// </summary>
public static class CommandRegistration
{
    /// <summary>Registers <typeparamref name="THandler"/> for <typeparamref name="TCommand"/> with the platform decorators.</summary>
    public static IServiceCollection AddCommand<TCommand, TResult, THandler>(this IServiceCollection services, CommandDescriptor descriptor)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        services.TryAddScoped<THandler>();
        services.AddScoped<ICommandHandler<TCommand, TResult>>(sp =>
        {
            var context = sp.GetRequiredService<RequestContext>();
            var clock = sp.GetRequiredService<IClock>();
            var session = sp.GetRequiredService<DbSession>();

            ICommandHandler<TCommand, TResult> handler = sp.GetRequiredService<THandler>();
            handler = new AuthorityDecorator<TCommand, TResult>(
                handler, sp.GetServices<ICommandAuthorization<TCommand>>(), sp.GetRequiredService<IAuthorityService>(), context, clock, descriptor);
            handler = new AuditDecorator<TCommand, TResult>(
                handler, sp.GetRequiredService<IAuditWriter>(), context, clock, descriptor, sp.GetServices<ICommandAuditor<TCommand, TResult>>());
            handler = new IdempotencyDecorator<TCommand, TResult>(handler, session, context, clock, descriptor);
            handler = new TransactionDecorator<TCommand, TResult>(handler, session, context, descriptor);
            handler = new ValidationDecorator<TCommand, TResult>(handler, sp.GetServices<IValidator<TCommand>>(), descriptor);
            return handler;
        });
        return services;
    }

    /// <summary>Registers the authority requirements of a command.</summary>
    public static IServiceCollection AddCommandAuthorization<TCommand, TAuthorization>(this IServiceCollection services)
        where TAuthorization : class, ICommandAuthorization<TCommand>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICommandAuthorization<TCommand>, TAuthorization>();
        return services;
    }

    /// <summary>Registers the audit description of a command.</summary>
    public static IServiceCollection AddCommandAuditor<TCommand, TResult, TAuditor>(this IServiceCollection services)
        where TAuditor : class, ICommandAuditor<TCommand, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICommandAuditor<TCommand, TResult>, TAuditor>();
        return services;
    }
}
