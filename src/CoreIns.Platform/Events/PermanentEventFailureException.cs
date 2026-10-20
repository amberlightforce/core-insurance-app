namespace CoreIns.Platform.Events;

/// <summary>
/// Thrown by an event handler for a failure that a retry cannot cure (a business refusal, a malformed event). The outbox
/// dispatcher parks the event in the dead-letter table at once instead of retrying it with backoff. Only when every failing
/// handler of the event reports a permanent failure; a mix of permanent and transient failures follows the normal retry path.
/// </summary>
public sealed class PermanentEventFailureException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PermanentEventFailureException()
    {
    }

    /// <summary>Creates the exception with a message (no personal data).</summary>
    public PermanentEventFailureException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    public PermanentEventFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
