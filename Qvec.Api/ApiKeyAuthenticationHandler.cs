using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

internal static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "ApiKey";
}

internal sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<QvecOptions> qvecOptions)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string HeaderName = "X-API-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authentication = qvecOptions.Value.Authentication;
        if (!authentication.Enabled)
        {
            return Task.FromResult(AuthenticateResult.Success(CreateTicket("local-development")));
        }

        if (!Request.Headers.TryGetValue(HeaderName, out var values) ||
            !string.Equals(values.ToString(), authentication.ApiKey, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid API key."));
        }

        return Task.FromResult(AuthenticateResult.Success(CreateTicket("api-key")));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Results.Problem(
            title: "Unauthorized",
            detail: "Missing or invalid API key.",
            statusCode: StatusCodes.Status401Unauthorized)
            .ExecuteAsync(Context);
    }

    private AuthenticationTicket CreateTicket(string name)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name)],
            Scheme.Name);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
    }
}