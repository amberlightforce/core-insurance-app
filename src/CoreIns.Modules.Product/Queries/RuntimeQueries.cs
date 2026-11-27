using System.Data;
using System.Text.Json;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Dapper;
using LocalizedText = CoreIns.Modules.Product.Contracts.Api.LocalizedText;

namespace CoreIns.Modules.Product.Queries;

/// <summary>
/// Runtime services of the product factory (BO-PFC-05): availability, describe and policy-draft validation. They read only
/// Locked (published), non-abstract versions of the caller's legal entity and respect the new-business and renewal windows.
/// </summary>
internal sealed class RuntimeQueries(DbSession session, RequestContext context, ILegalEntityDirectory legalEntities, ProductReader reader)
{
    /// <summary><c>pfc.Availability.check</c>: the sellable products for a channel and transaction type on <paramref name="validAt"/>.</summary>
    public async Task<Result<AvailabilityCheckResponse>> CheckAvailabilityAsync(AvailabilityCheckRequest request, BusinessDate validAt, CancellationToken cancellationToken)
    {
        if (!TryTransaction(request.TransactionType?.ToString(), out var renewal))
        {
            return BadTransaction();
        }

        var rows = await InWindowAsync(request.Channel, renewal, validAt, request.Product, request.CustomerType, cancellationToken).ConfigureAwait(false);
        var offerings = rows.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => new AvailabilityOffering
        {
            Product = r.Code,
            Name = new LocalizedText { El = r.NameEl, En = r.NameEn },
            ProductType = r.ProductType,
            CustomerType = r.CustomerType,
            Version = new ProductVersionNumber(r.Major, r.Minor),
            ArtefactHash = Sha256Hash.Parse(r.ArtefactHash),
            ContractCurrency = Currency.FromCode(r.ContractCurrency),
        }).ToList();
        return new AvailabilityCheckResponse { Available = offerings.Count > 0, Reason = offerings.Count > 0 ? null : "NO-VERSION-IN-WINDOW", Offerings = offerings };
    }

    /// <summary><c>pfc.Product.describe</c>: the published version in force (by product) or the Locked version with this hash.</summary>
    public async Task<Result<ProductDescribeResponse>> DescribeAsync(ProductDescribeRequest request, BusinessDate validAt, CancellationToken cancellationToken)
    {
        string hash;
        if (request.Hash is { } given)
        {
            if (!await IsLockedAsync(given.Value, cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.PFC, "NO-VERSION", "No published version with this artefact hash exists for this legal entity.");
            }

            hash = given.Value;
        }
        else if (!string.IsNullOrEmpty(request.Product) && !string.IsNullOrEmpty(request.Channel))
        {
            if (!TryTransaction(request.TransactionType?.ToString(), out var renewal))
            {
                return BadTransaction();
            }

            var rows = await InWindowAsync(request.Channel, renewal, validAt, request.Product, null, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return DomainError.Of(ModuleCode.PFC, "NO-VERSION",
                    $"No published version of '{request.Product}' is in force for channel {request.Channel} on {validAt} ({(renewal ? "renewal" : "new business")}).");
            }

            hash = rows[0].ArtefactHash;
        }
        else
        {
            return DomainError.Of(ModuleCode.PFC, "VALIDATION", "Give a product with a channel, or an artefact hash.");
        }

        var artefact = await reader.GetArtefactAsync(hash, cancellationToken).ConfigureAwait(false);
        if (artefact is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "No artefact with this hash exists for this legal entity.");
        }

        return Describe(artefact, request.Channel);
    }

    /// <summary><c>pfc.PolicyDraft.validate</c>: a draft against a Locked artefact; findings are returned, not thrown.</summary>
    public async Task<Result<PolicyDraftValidateResponse>> ValidateDraftAsync(PolicyDraftValidateRequest request, CancellationToken cancellationToken)
    {
        if (!await IsLockedAsync(request.Hash.Value, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "No published version with this artefact hash exists for this legal entity.");
        }

        var artefact = await reader.GetArtefactAsync(request.Hash.Value, cancellationToken).ConfigureAwait(false);
        if (artefact is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH", "No artefact with this hash exists for this legal entity.");
        }

        PolicyDraft? draft;
        try
        {
            draft = request.Draft.Deserialize<PolicyDraft>(SharedKernelJson.Options);
        }
        catch (JsonException)
        {
            draft = null;
        }

        if (draft is null)
        {
            return DomainError.Of(ModuleCode.PFC, "VALIDATION", "The draft does not have the policy-draft shape (elements and coverages).");
        }

        var checkpoint = request.Checkpoint is { ValueKind: JsonValueKind.String } stage ? stage.GetString() : null;
        var issues = PolicyDraftValidator.Validate(artefact.Artefact, draft, checkpoint);
        return new PolicyDraftValidateResponse { Valid = issues.All(i => i.Severity != PolicyDraftIssue.SeverityValue.Error), Errors = issues };
    }

    private static Result<ProductDescribeResponse> Describe(CompiledArtefact compiled, string? channel)
    {
        var a = compiled.Artefact;
        var steps = new List<DescribeStep>
        {
            new() { Code = "RISK", Kind = "ELEMENTS", QuestionSet = null, Name = new LocalizedText { El = "Στοιχεία κινδύνου", En = "Risk details" } },
        };
        steps.AddRange(a.QuestionSets.Select(q => new DescribeStep { Code = q.Code, Kind = "QUESTIONS", QuestionSet = q.Code, Name = q.Name }));
        steps.Add(new DescribeStep { Code = "COVERAGES", Kind = "COVERAGES", QuestionSet = null, Name = new LocalizedText { El = "Καλύψεις", En = "Coverages" } });

        var plans = channel is not null && a.References.PaymentPlans?.TryGetValue(channel, out var forChannel) == true
            ? forChannel.ToList()
            : (a.References.PaymentPlans?.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList() ?? []);
        var illustrative = a.QuestionSets.SelectMany(q => q.Questions).Any(q => q.Illustrative == true)
            || a.Coverages.SelectMany(c => c.Terms).Any(t => t.Options?.Any(o => o.Illustrative == true) == true)
            || (a.RefundMethods?.Any(r => r.Illustrative == true) == true);
        return new ProductDescribeResponse
        {
            Product = a.Product.Code,
            Name = a.Product.Name,
            ProductType = a.Product.ProductType,
            CustomerType = a.Product.CustomerType,
            Version = a.Version,
            ArtefactHash = compiled.Hash,
            IsAbstract = a.IsAbstract,
            Channels = a.Channels,
            Languages = a.Languages,
            ContractCurrency = a.ContractCurrency,
            Terms = a.Terms,
            Windows = a.Windows,
            Illustrative = illustrative,
            OrderedSteps = steps,
            Elements = a.Elements,
            Coverages = a.Coverages.OrderBy(c => c.DisplayOrder ?? int.MaxValue).ToList(),
            QuestionSets = a.QuestionSets,
            PaymentPlans = plans,
        };
    }

    private static bool TryTransaction(string? value, out bool renewal)
    {
        renewal = string.Equals(value, "Renewal", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static DomainError BadTransaction() =>
        DomainError.Of(ModuleCode.PFC, "VALIDATION", "transactionType must be NewBusiness or Renewal.");

    private LegalEntityId LegalEntity() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private async Task<bool> IsLockedAsync(string hash, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pfc.product_version WHERE legal_entity_id = @le AND artefact_hash = @hash AND status = 'LOCKED')",
            new { le = LegalEntity().Value, hash }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The newest Locked, non-abstract version per product whose window (new business or renewal) covers the date and that is sold on the channel.</summary>
    private async Task<List<WindowRow>> InWindowAsync(
        string channel, bool renewal, BusinessDate validAt, string? product, string? customerType, CancellationToken cancellationToken)
    {
        var from = renewal ? "v.renewal_from" : "v.new_business_from";
        var to = renewal ? "v.renewal_to" : "v.new_business_to";
        var args = new DynamicParameters(new { le = LegalEntity().Value, channel, product, customerType });
        args.Add("validAt", validAt.Value, DbType.Date);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<WindowRow>(new CommandDefinition(
            $"""
             SELECT DISTINCT ON (p.product_id)
                    p.code AS Code, p.name_el AS NameEl, p.name_en AS NameEn, p.product_type AS ProductType, p.customer_type AS CustomerType,
                    v.major AS Major, v.minor AS Minor, v.artefact_hash AS ArtefactHash, v.contract_currency AS ContractCurrency
               FROM pfc.product p
               JOIN pfc.product_version v ON v.product_id = p.product_id
              WHERE p.legal_entity_id = @le AND v.status = 'LOCKED' AND NOT v.is_abstract AND @channel = ANY (v.channels)
                AND {from} <= @validAt AND ({to} IS NULL OR @validAt < {to})
                AND (@product::text IS NULL OR p.code = @product) AND (@customerType::text IS NULL OR p.customer_type = @customerType)
              ORDER BY p.product_id, v.major DESC, v.minor DESC
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    private sealed class WindowRow
    {
        public string Code { get; set; } = string.Empty;

        public string NameEl { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string ProductType { get; set; } = string.Empty;

        public string CustomerType { get; set; } = string.Empty;

        public int Major { get; set; }

        public int Minor { get; set; }

        public string ArtefactHash { get; set; } = string.Empty;

        public string ContractCurrency { get; set; } = string.Empty;
    }
}
