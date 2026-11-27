using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Channels.Api;

/// <summary>REST facade of <c>chn.Notification.*</c>, <c>chn.NotificationSubscription.*</c> and <c>chn.Message.*</c> (BO-CHN-04).</summary>
[ApiController]
[Route("api/chn/v1")]
internal sealed class PortalInboxController(PortalInbox inbox) : ControllerBase
{
    /// <summary>chn.Notification.list.</summary>
    [HttpGet("notifications")]
    [Authorize(Policy = "chn.Notification.list")]
    public async Task<IResult> NotificationsAsync([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        await inbox.NotificationsAsync(cursor, limit, cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.Notification.markRead.</summary>
    [HttpPost("notifications/mark-read")]
    [Authorize(Policy = "chn.Notification.markRead")]
    public async Task<IResult> MarkReadAsync(
        [FromBody] NotificationMarkReadRequest request, [FromServices] ICommandHandler<MarkNotificationsRead, NotificationMarkReadResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new MarkNotificationsRead(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.NotificationSubscription.list.</summary>
    [HttpGet("notification-subscriptions")]
    [Authorize(Policy = "chn.NotificationSubscription.list")]
    public async Task<IResult> SubscriptionsAsync(CancellationToken cancellationToken) =>
        await inbox.SubscriptionsAsync(cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.NotificationSubscription.set.</summary>
    [HttpPost("notification-subscriptions/set")]
    [Authorize(Policy = "chn.NotificationSubscription.set")]
    public async Task<IResult> SetSubscriptionAsync(
        [FromBody] NotificationSubscriptionSetRequest request, [FromServices] ICommandHandler<SetNotificationSubscription, NotificationSubscriptionSetResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetNotificationSubscription(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Message.send.</summary>
    [HttpPost("messages/send")]
    [Authorize(Policy = "chn.Message.send")]
    public async Task<IResult> SendAsync(
        [FromBody] MessageSendRequest request, [FromServices] ICommandHandler<SendMessage, MessageSendResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SendMessage(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.Message.list: a customer sees their own threads, staff see every thread.</summary>
    [HttpGet("messages")]
    [Authorize(Policy = "chn.Message.list")]
    public async Task<IResult> MessagesAsync(
        [FromQuery] string? topic, [FromQuery(Name = "object")] string? policy, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        await inbox.MessagesAsync(topic, policy, cursor, limit, cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.Message.reply: a staff member answers a thread.</summary>
    [HttpPost("messages/reply")]
    [Authorize(Policy = "chn.Message.reply")]
    public async Task<IResult> ReplyAsync(
        [FromBody] MessageReplyRequest request, [FromServices] ICommandHandler<ReplyToMessage, MessageReplyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReplyToMessage(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
