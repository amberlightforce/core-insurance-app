using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Channels.Queries;
using CoreIns.Modules.Channels.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Channels.Commands;

/// <summary>Errors and the notification writer shared by the portal engagement commands and the event handlers.</summary>
internal static class PortalEngagementSupport
{
    public static DomainError NotFound() => DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "Not found.");

    public static DomainError NotPermitted() => DomainError.Of(ModuleCode.CHN, "NOT-PERMITTED", "You are not permitted to do this.");

    /// <summary>
    /// Queues one notification for the party (the caller saves). Skipped when the party switched the class off or when the same source already
    /// produced one, so a redelivered event is a no-op. Holds no personal data.
    /// </summary>
    public static async Task AddNotificationAsync(
        ChannelsDbContext db, LegalEntityId legalEntity, PartyId party, string kind, string? subjectType, string? subjectId, string sourceKey, Instant now, CancellationToken cancellationToken)
    {
        var notificationClass = NotificationKinds.ClassOf(kind);
        if (await db.NotificationSubscriptions.AsNoTracking()
                .AnyAsync(s => s.LegalEntityId == legalEntity && s.PartyId == party && s.NotificationClass == notificationClass && !s.Enabled, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await db.Notifications.AsNoTracking().AnyAsync(n => n.LegalEntityId == legalEntity && n.PartyId == party && n.Kind == kind && n.SourceKey == sourceKey, cancellationToken).ConfigureAwait(false)
            || db.Notifications.Local.Any(n => n.LegalEntityId == legalEntity && n.PartyId == party && n.Kind == kind && n.SourceKey == sourceKey))
        {
            return;
        }

        db.Notifications.Add(new NotificationRow
        {
            NotificationId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PartyId = party, Kind = kind, NotificationClass = notificationClass,
            SubjectType = subjectType, SubjectId = subjectId, SourceKey = sourceKey, CreatedAt = now,
        });
    }
}

// ---- chn.Notification.markRead ---------------------------------------------------------------------------------------------------

internal sealed record MarkNotificationsRead(NotificationMarkReadRequest Request) : ICommand<NotificationMarkReadResponse>;

internal sealed class MarkNotificationsReadValidator : AbstractValidator<MarkNotificationsRead>
{
    public MarkNotificationsReadValidator()
    {
        RuleFor(c => c.Request).Must(r => r.All == true || (r.NotificationIds is { Count: > 0 and <= 200 })).WithMessage("Give notificationIds (at most 200) or all = true.");
    }
}

/// <summary>Marks the caller's own notifications read. Idempotent: already-read items keep their first read time and are not counted again.</summary>
internal sealed class MarkNotificationsReadHandler(PortalScope scope, PortalInbox inbox, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<MarkNotificationsRead, NotificationMarkReadResponse>
{
    public async Task<Result<NotificationMarkReadResponse>> HandleAsync(MarkNotificationsRead command, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalEngagementSupport.NotFound();
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.Notifications.Where(n => n.LegalEntityId == entity && n.PartyId == caller.PartyId && n.ReadAt == null);
        if (command.Request.All != true)
        {
            var ids = command.Request.NotificationIds!.ToList();
            query = query.Where(n => ids.Contains(n.NotificationId));
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        foreach (var row in rows)
        {
            row.ReadAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationMarkReadResponse { Updated = rows.Count, UnreadCount = await inbox.UnreadCountAsync(caller.PartyId, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class MarkNotificationsReadAuditor : ICommandAuditor<MarkNotificationsRead, NotificationMarkReadResponse>
{
    public CommandAuditFacts Describe(MarkNotificationsRead command, Result<NotificationMarkReadResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("scope", command.Request.All == true ? "all" : "ids") };
}

// ---- chn.NotificationSubscription.set --------------------------------------------------------------------------------------------

internal sealed record SetNotificationSubscription(NotificationSubscriptionSetRequest Request) : ICommand<NotificationSubscriptionSetResponse>;

internal sealed class SetNotificationSubscriptionHandler(
    PortalScope scope, PortalInbox inbox, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<SetNotificationSubscription, NotificationSubscriptionSetResponse>
{
    public async Task<Result<NotificationSubscriptionSetResponse>> HandleAsync(SetNotificationSubscription command, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalEngagementSupport.NotFound();
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var wire = command.Request.NotificationClass switch
        {
            NotificationSubscriptionSetRequest.NotificationClassValue.Renewal => NotificationKinds.ClassRenewal,
            NotificationSubscriptionSetRequest.NotificationClassValue.Billing => NotificationKinds.ClassBilling,
            NotificationSubscriptionSetRequest.NotificationClassValue.Claim => NotificationKinds.ClassClaim,
            NotificationSubscriptionSetRequest.NotificationClassValue.Document => NotificationKinds.ClassDocument,
            _ => NotificationKinds.ClassMessage,
        };
        var row = await db.NotificationSubscriptions.SingleOrDefaultAsync(s => s.LegalEntityId == entity && s.PartyId == caller.PartyId && s.NotificationClass == wire, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.NotificationSubscriptions.Add(new NotificationSubscriptionRow
            {
                SubscriptionId = Guid.CreateVersion7(), LegalEntityId = entity, PartyId = caller.PartyId, NotificationClass = wire, Enabled = command.Request.Enabled, UpdatedAt = clock.Now,
            });
        }
        else if (row.Enabled != command.Request.Enabled)
        {
            row.Enabled = command.Request.Enabled;
            row.UpdatedAt = clock.Now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var all = await inbox.SubscriptionsOfAsync(caller.PartyId, cancellationToken).ConfigureAwait(false);
        return new NotificationSubscriptionSetResponse { Subscriptions = all.Subscriptions };
    }
}

internal sealed class SetNotificationSubscriptionAuditor : ICommandAuditor<SetNotificationSubscription, NotificationSubscriptionSetResponse>
{
    public CommandAuditFacts Describe(SetNotificationSubscription command, Result<NotificationSubscriptionSetResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("notificationClass", command.Request.NotificationClass.ToString()).With("enabled", command.Request.Enabled ? "true" : "false") };
}

// ---- chn.Message.send ------------------------------------------------------------------------------------------------------------

internal sealed record SendMessage(MessageSendRequest Request) : ICommand<MessageSendResponse>;

internal sealed class SendMessageValidator : AbstractValidator<SendMessage>
{
    public SendMessageValidator()
    {
        RuleFor(c => c.Request.Text).NotEmpty().Must(t => t.Trim().Length is >= 1 and <= MessageValues.MaxTextLength).WithMessage("The message has 1-4000 characters.");
    }
}

/// <summary>
/// A customer writes to the insurer. A new thread may reference one of the customer's own policies (anything else is a 404, like a policy
/// detail); a thread id continues one of the customer's own threads. Staff cannot use this endpoint (their reply is <c>chn.Message.reply</c>).
/// </summary>
internal sealed class SendMessageHandler(PortalScope scope, PortalInbox inbox, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<SendMessage, MessageSendResponse>
{
    public async Task<Result<MessageSendResponse>> HandleAsync(SendMessage command, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalEngagementSupport.NotFound();
        }

        var request = command.Request;
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        Guid threadId;
        string topic;
        PolicyId? policy;
        if (request.ThreadId is { } existing)
        {
            if (await inbox.ThreadAsync(existing, caller.PartyId, cancellationToken).ConfigureAwait(false) is not { } thread)
            {
                return PortalEngagementSupport.NotFound();
            }

            threadId = existing;
            topic = thread.Topic.ToString().ToUpperInvariant();
            policy = thread.PolicyId;
        }
        else
        {
            threadId = Guid.CreateVersion7();
            topic = request.Topic?.ToString().ToUpperInvariant() ?? "GENERAL";
            policy = request.PolicyId;
            if (policy is { } wanted && !await scope.OwnsPolicyAsync(wanted, cancellationToken).ConfigureAwait(false))
            {
                return PortalEngagementSupport.NotFound();
            }
        }

        // TODO(hardening): rate-limit messages per customer and scan the text; link the thread to a WRK request for staff routing.
        db.Messages.Add(new MessageRow
        {
            MessageId = Guid.CreateVersion7(), LegalEntityId = entity, ThreadId = threadId, PartyId = caller.PartyId, Direction = MessageValues.Customer, Topic = topic, PolicyId = policy,
            Text = request.Text.Trim(), SentAt = clock.Now, SentBy = context.Actor.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var saved = await inbox.ThreadAsync(threadId, caller.PartyId, cancellationToken).ConfigureAwait(false);
        return new MessageSendResponse { Thread = saved! with { PartyId = null } };
    }
}

internal sealed class SendMessageAuditor : ICommandAuditor<SendMessage, MessageSendResponse>
{
    // The message text is personal data: only the thread id is audited.
    public CommandAuditFacts Describe(SendMessage command, Result<MessageSendResponse>? result) =>
        new() { BusinessKeys = result is { IsSuccess: true } ok ? BusinessKeys.Empty.With("threadId", ok.Value.Thread.ThreadId.ToString("D")) : BusinessKeys.Empty };
}

// ---- chn.Message.reply -----------------------------------------------------------------------------------------------------------

internal sealed record ReplyToMessage(MessageReplyRequest Request) : ICommand<MessageReplyResponse>;

internal sealed class ReplyToMessageValidator : AbstractValidator<ReplyToMessage>
{
    public ReplyToMessageValidator()
    {
        RuleFor(c => c.Request.ThreadId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Text).NotEmpty().Must(t => t.Trim().Length is >= 1 and <= MessageValues.MaxTextLength).WithMessage("The reply has 1-4000 characters.");
    }
}

/// <summary>A staff member answers a customer thread; the customer gets an in-app notification (unless they switched messages off).</summary>
internal sealed class ReplyToMessageHandler(PortalInbox inbox, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<ReplyToMessage, MessageReplyResponse>
{
    public async Task<Result<MessageReplyResponse>> HandleAsync(ReplyToMessage command, CancellationToken cancellationToken)
    {
        if (!PortalScope.IsStaff(context))
        {
            return PortalEngagementSupport.NotPermitted();
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        if (await inbox.ThreadAsync(command.Request.ThreadId, null, cancellationToken).ConfigureAwait(false) is not { } thread)
        {
            return PortalEngagementSupport.NotFound();
        }

        var now = clock.Now;
        var message = new MessageRow
        {
            MessageId = Guid.CreateVersion7(), LegalEntityId = entity, ThreadId = thread.ThreadId, PartyId = thread.PartyId!.Value, Direction = MessageValues.Staff,
            Topic = thread.Topic.ToString().ToUpperInvariant(), PolicyId = thread.PolicyId, Text = command.Request.Text.Trim(), SentAt = now, SentBy = context.Actor.ToString(),
        };
        db.Messages.Add(message);
        await PortalEngagementSupport.AddNotificationAsync(
            db, entity, message.PartyId, NotificationKinds.MessageReply, "MESSAGE_THREAD", thread.ThreadId.ToString("D"), message.MessageId.ToString("D"), now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new MessageReplyResponse { Thread = (await inbox.ThreadAsync(thread.ThreadId, null, cancellationToken).ConfigureAwait(false))! };
    }
}

internal sealed class ReplyToMessageAuditor : ICommandAuditor<ReplyToMessage, MessageReplyResponse>
{
    public CommandAuditFacts Describe(ReplyToMessage command, Result<MessageReplyResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("threadId", command.Request.ThreadId.ToString("D")) };
}
