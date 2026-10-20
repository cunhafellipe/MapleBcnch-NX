using MapleBench.Models;

namespace MapleBench.Api;

/// <summary>
/// The one place an exception becomes an HTTP response.
/// </summary>
/// <remarks>
/// <para>
/// This used to exist twice, as a near-verbatim private method in
/// <c>Endpoints.cs</c> and another in <c>DumpEndpoints.cs</c>, each registered on its own
/// <c>/api</c> group. That is not a style complaint — it is a measured defect. The
/// <c>InvalidDataException</c> branch was added to one of them on 2026-10-20 and the other
/// was not updated, so a corrupt archive answered 400 through <c>/api/files/open</c> and
/// 500 with the real sentence hidden in <c>detail</c> through <c>/api/dump/*</c>. Two copies
/// will drift; the fix for drift is one copy.
/// </para>
/// <para>
/// The branches are ordered most-specific to least. <see cref="InvalidDataException"/>
/// derives straight from <see cref="SystemException"/> — not from
/// <see cref="IOException"/> and not from <see cref="ArgumentException"/> — so it matches
/// nothing above it and used to fall to the catch-all. Every bounds check in
/// <c>Services/Nx</c> throws it, which is what made a corrupt NX and a missing one answer
/// identically.
/// </para>
/// <para>
/// The catch-all writes the exception to stderr as well as answering. The message alone
/// cost hours once: "Object reference not set to an instance of an object" arrived from
/// four different places while chasing 113 failing skills, and none of them were
/// distinguishable from the response alone. The response stays short because agents read
/// it; the detail field carries the sentence for a person who looks.
/// </para>
/// </remarks>
internal static class ApiErrorFilter
{
    /// <summary>
    /// Runs <paramref name="next"/> and converts anything it throws into the shape the UI
    /// expects. Register with <c>AddEndpointFilter</c>.
    /// </summary>
    public static async ValueTask<object?> Filter(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(new ApiError(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            // The file was there and the request named it correctly; what is wrong is
            // what is inside it. A bad request, not a missing resource — answering 404
            // here would send the user hunting for a file that exists on disk.
            return Results.BadRequest(new ApiError(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ApiError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiError(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new ApiError("Access denied.", ex.Message), statusCode: 403);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[endpoint] {ex}");
            return Results.Json(
                new ApiError("Something went wrong.", ex.Message), statusCode: 500);
        }
    }
}