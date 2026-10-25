using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure.Options;

public sealed class TradingInstrumentOptionsValidator : IValidateOptions<TradingSettings>
{
    public ValidateOptionsResult Validate(string? name, TradingSettings options)
    {
        var errors = options.GetValidationErrors();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
