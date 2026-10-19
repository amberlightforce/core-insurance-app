using CoreIns.Modules.Reinsurance.Contracts;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// The in-process contract <see cref="IReinsuranceContractService"/> (D-ARC-16) other modules call: the registry reads
/// (<c>get</c>, <c>list</c>, <c>applicable</c>, which SL4-RI-RECOVERY uses). <c>versionAt</c> belongs to the versioning work
/// package and fails closed with <c>RI-ERR-NOT-AVAILABLE</c> until then.
/// </summary>
internal sealed class ReinsuranceContractService(ContractReader reader) : IReinsuranceContractService
{
    public async Task<ContractApplicableResponse> ApplicableAsync(string productCode, string coverageCode, ValidAt? validAt = null, CancellationToken cancellationToken = default) =>
        Unwrap(await reader.ApplicableAsync(productCode, coverageCode, validAt, cancellationToken).ConfigureAwait(false));

    public async Task<ContractGetResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var view = Guid.TryParse(id, out var contractId) ? await reader.GetAsync(contractId, cancellationToken).ConfigureAwait(false) : null;
        return view is null ? throw new DomainException(RiErrors.NotFound()) : new ContractGetResponse { Contract = view };
    }

    public async Task<ContractListPage> ListAsync(
        string? cursor = null, int? limit = null, int? contractYear = null, RiContractStatus? status = null, RiContractType? contractType = null,
        CancellationToken cancellationToken = default) =>
        Unwrap(await reader.ListAsync(cursor, limit, contractYear, status, contractType, cancellationToken).ConfigureAwait(false));

    public Task<ContractVersionAtResponse> VersionAtAsync(CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.RI, "NOT-AVAILABLE", "ri.Contract.versionAt is built with contract versioning (a later work package)."));

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
}
