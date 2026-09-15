using Microsoft.Extensions.Options;

internal static class AotOptionsBuilderExtensions
{
    public static OptionsBuilder<QvecOptions> ValidateDataAnnotations(this OptionsBuilder<QvecOptions> builder)
    {
        builder.Services.AddSingleton<IValidateOptions<QvecOptions>, ValidateQvecOptions>();
        return builder;
    }
}