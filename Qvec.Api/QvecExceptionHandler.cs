using Microsoft.AspNetCore.Diagnostics;
using Qvec.Core;

internal sealed class QvecExceptionHandler(ILogger<QvecExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title, logAsError) = exception switch
        {
            QvecDimensionException => (StatusCodes.Status400BadRequest, "Invalid vector dimension", false),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", false),
            QvecFullException => (StatusCodes.Status409Conflict, "Database capacity exhausted", false),
            QvecFormatException => (StatusCodes.Status500InternalServerError, "Database format error", true),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error", true)
        };

        if (logAsError)
        {
            logger.LogError(exception, "Unhandled API exception mapped to {StatusCode}.", statusCode);
        }
        else
        {
            logger.LogInformation(exception, "Request exception mapped to {StatusCode}.", statusCode);
        }

        await Results.Problem(
            title: title,
            detail: logAsError ? "An unexpected server error occurred." : exception.Message,
            statusCode: statusCode)
            .ExecuteAsync(httpContext);

        return true;
    }
}