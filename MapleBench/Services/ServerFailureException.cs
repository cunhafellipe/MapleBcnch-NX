namespace MapleBench.Services;

/// <summary>
/// The server could not carry out a request that was itself well formed.
/// </summary>
/// <remarks>
/// <para>
/// This is the exception for the case where <b>the request was valid and the
/// server's own work came out wrong</b>: an archive it wrote did not read
/// back, a serialisation produced nothing, a hash could not be computed, a
/// rename referred to a node that had moved, an encoder produced a different
/// format than the one asked for.
/// </para>
/// <para>
/// The reason this type exists is that <see cref="InvalidOperationException"/>
/// is mapped to <c>400 Bad Request</c> by <c>ApiErrorFilter</c>, and that
/// mapping is right for the overwhelming majority of the places that throw it
/// — a bad path, a missing file, a name that does not resolve. Those are the
/// caller's to fix.
/// </para>
/// <para>
/// But a handful of sites use the same exception to report <em>the server's
/// own failure</em>, and those were answering 400 = "your request was
/// malformed" for a condition the caller could not have prevented and could
/// not have repaired. The user would go looking at their own request while
/// the real problem was in the file the server had just written.
/// </para>
/// <para>
/// <b>This type derives from <see cref="InvalidOperationException"/> on
/// purpose.</b> That keeps every existing <c>catch (InvalidOperationException)</c>
/// in the codebase working exactly as before — the endpoints that answer 409
/// for a busy job, and the services that swallow a hashing failure and treat
/// it as "could not be compared" — so nothing outside the HTTP mapping
/// changes behaviour. Only the error filter sees the difference, and it sees
/// it by testing for this type first.
/// </para>
/// </remarks>
public sealed class ServerFailureException : InvalidOperationException
{
    /// <summary>Creates the exception with a message describing what the server could not do.</summary>
    public ServerFailureException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the failure that caused it.</summary>
    public ServerFailureException(string message, Exception innerException)
        : base(message, innerException) { }
}
