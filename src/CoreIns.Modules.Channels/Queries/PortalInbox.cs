using System.Globalization;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Channels.Services;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Channels.Queries;

/// <summary>Maps the upper-snake wire values stored in CHN rows to the generated contract enums.</summary>
internal static class Wire
{
    public static T Enum<T>(string wire) where T : struct, Enum => System.Enum.Parse<T>(string.Concat(wire.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant())));
}

/// <summary>
/// The customer's in-app notifications and secure messages (BO-CHN-04). Customers see only rows of their own linked party (a household member
/// never reads another's inbox); staff, who are governed by permissions rather than ownership, see every thread of the legal entity.
/// Every method returns null (404) for a caller who is neither a linked customer nor staff.
/// </summary>
internal sealed class PortalInbox(PortalScope scope, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public async Task<NotificationListPage?> NotificationsAsync(string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var entity = LegalEntity;
        var query = db.Notifications.AsNoTracking().Where(n => n.LegalEntityId == entity && n.PartyId == caller.PartyId);
        var rows = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.NotificationId).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Take(size).Select(n => new NotificationListItem
        {
            NotificationId = n.NotificationId,
            Kind = Wire.Enum<NotificationListItem.KindValue>(n.Kind),
            NotificationClass = Wire.Enum<NotificationListItem.NotificationClassValue>(n.NotificationClass),
            SubjectType = n.SubjectType,
            SubjectId = n.SubjectId,
            CreatedAt = n.CreatedAt,
            ReadAt = n.ReadAt,
        }).ToList();
        return new NotificationListPage
        {
            Items = items,
            NextCursor = rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null,
            Limit = size,
            UnreadCount = await UnreadCountAsync(caller.PartyId, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<NotificationSubscriptionListResponse?> SubscriptionsAsync(CancellationToken cancellationToken) =>
        await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is { } caller
            ? await SubscriptionsOfAsync(caller.PartyId, cancellationToken).ConfigureAwait(false)
            : null;

    public async Task<NotificationSubscriptionListResponse> SubscriptionsOfAsync(PartyId party, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var rows = await db.NotificationSubscriptions.AsNoTracking().Where(s => s.LegalEntityId == entity && s.PartyId == party).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationSubscriptionListResponse
        {
            Subscriptions =
            [
                .. NotificationKinds.Classes.Select(c => new NotificationSubscriptionItem
                {
                    NotificationClass = Wire.Enum<NotificationSubscriptionItem.NotificationClassValue>(c),
                    Enabled = rows.FirstOrDefault(r => r.NotificationClass == c)?.Enabled ?? true,
                }),
            ],
        };
    }

    public Task<int> UnreadCountAsync(PartyId party, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        return db.Notifications.AsNoTracking().CountAsync(n => n.LegalEntityId == entity && n.PartyId == party && n.ReadAt == null, cancellationToken);
    }

    /// <summary>Threads the customer started whose last message is theirs: the dashboard's open requests.</summary>
    public async Task<IReadOnlyList<PortalOpenRequest>> OpenRequestsAsync(PartyId party, CancellationToken cancellationToken)
    {
        var threads = await LoadThreadsAsync(party, null, null, 0, 20, cancellationToken).ConfigureAwait(false);
        return
        [
            .. threads.Where(t => t.AwaitingStaff).Select(t => new PortalOpenRequest
            {
                ThreadId = t.ThreadId, Topic = Wire.Enum<PortalOpenRequest.TopicValue>(t.Topic.ToString().ToUpperInvariant()), LastMessageAt = t.LastMessageAt,
            }),
        ];
    }

    public async Task<MessageListPage?> MessagesAsync(string? topic, string? policy, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        PartyId? party = null;
        if (!PortalScope.IsStaff(context))
        {
            if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
            {
                return null;
            }

            party = caller.PartyId;
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var topicFilter = topic is null ? null : topic.Trim().ToUpperInvariant();
        PolicyId? policyFilter = Guid.TryParse(policy, out var guid) ? new PolicyId(guid) : null;
        var threads = await LoadThreadsAsync(party, topicFilter, policyFilter, offset, size + 1, cancellationToken).ConfigureAwait(false);
        return new MessageListPage
        {
            Items = [.. threads.Take(size).Select(t => new MessageListItem { Thread = party is null ? t : t with { PartyId = null } })],
            NextCursor = threads.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
    }

    /// <summary>One thread with all its messages; <paramref name="party"/> restricts it to the customer's own (null for staff).</summary>
    public async Task<MessageThread?> ThreadAsync(Guid threadId, PartyId? party, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var rows = await db.Messages.AsNoTracking().Where(m => m.LegalEntityId == entity && m.ThreadId == threadId).OrderBy(m => m.SentAt).ThenBy(m => m.MessageId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 || (party is { } p && rows[0].PartyId != p) ? null : ToThread(rows);
    }

    private async Task<List<MessageThread>> LoadThreadsAsync(PartyId? party, string? topic, PolicyId? policy, int offset, int take, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var scoped = db.Messages.AsNoTracking().Where(m => m.LegalEntityId == entity);
        if (party is { } p)
        {
            scoped = scoped.Where(m => m.PartyId == p);
        }

        if (topic is not null)
        {
            scoped = scoped.Where(m => m.Topic == topic);
        }

        if (policy is { } pol)
        {
            scoped = scoped.Where(m => m.PolicyId == pol);
        }

        var threadIds = await scoped.GroupBy(m => m.ThreadId).Select(g => new { ThreadId = g.Key, Last = g.Max(m => m.SentAt) })
            .OrderByDescending(g => g.Last).ThenByDescending(g => g.ThreadId).Skip(offset).Take(take).Select(g => g.ThreadId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Messages.AsNoTracking().Where(m => m.LegalEntityId == entity && threadIds.Contains(m.ThreadId)).OrderBy(m => m.SentAt).ThenBy(m => m.MessageId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byThread = rows.GroupBy(m => m.ThreadId).ToDictionary(g => g.Key, g => g.ToList());
        return [.. threadIds.Where(byThread.ContainsKey).Select(id => ToThread(byThread[id]))];
    }

    public static MessageThread ToThread(IReadOnlyList<MessageRow> rows)
    {
        var first = rows[0];
        var last = rows[^1];
        return new MessageThread
        {
            ThreadId = first.ThreadId,
            Topic = Wire.Enum<MessageThread.TopicValue>(first.Topic),
            PolicyId = first.PolicyId,
            PartyId = first.PartyId,
            LastMessageAt = last.SentAt,
            AwaitingStaff = last.Direction == MessageValues.Customer,
            Messages = [.. rows.Select(m => new MessageItem { MessageId = m.MessageId, Direction = Wire.Enum<MessageItem.DirectionValue>(m.Direction), Text = m.Text, SentAt = m.SentAt })],
        };
    }
}
