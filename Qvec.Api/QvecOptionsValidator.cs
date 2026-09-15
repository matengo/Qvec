using Microsoft.Extensions.Options;
using Qvec.Core;

internal sealed class QvecOptionsValidator : IValidateOptions<QvecOptions>
{
    public ValidateOptionsResult Validate(string? name, QvecOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add("Qvec:Path is required.");
        }

        if (!Enum.IsDefined(typeof(DistanceFunction), options.DistanceFunction))
        {
            failures.Add("Qvec:DistanceFunction must be DotProduct or Cosine.");
        }

        if (options.Authentication.Enabled && string.IsNullOrWhiteSpace(options.Authentication.ApiKey))
        {
            failures.Add("Qvec:Authentication:ApiKey is required when authentication is enabled. Set Qvec:Authentication:Enabled=false only for explicit local-development opt-out.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}