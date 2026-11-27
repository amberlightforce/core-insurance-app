using CoreIns.Modules.Migration.Services.Mapping;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Mapping.create: stores a new draft version of a mapping spec with its code tables (BO-MIG-04).</summary>
internal sealed record CreateMappingSpec(MappingCreateBody Body) : ICommand<MappingSpecView>;

internal sealed class CreateMappingSpecHandler(MappingService service) : ICommandHandler<CreateMappingSpec, MappingSpecView>
{
    public Task<Result<MappingSpecView>> HandleAsync(CreateMappingSpec command, CancellationToken cancellationToken) => service.CreateAsync(command.Body, cancellationToken);
}

/// <summary>mig.Mapping.publish: puts a draft spec forward (maker) or approves/rejects a pending one (a different checker).</summary>
internal sealed record PublishMappingSpec(MappingPublishBody Body) : ICommand<MappingSpecView>;

internal sealed class PublishMappingSpecHandler(MappingService service) : ICommandHandler<PublishMappingSpec, MappingSpecView>
{
    public Task<Result<MappingSpecView>> HandleAsync(PublishMappingSpec command, CancellationToken cancellationToken) => service.PublishAsync(command.Body, cancellationToken);
}
