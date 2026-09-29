using FluentValidation;
using AppForge.Api.Features.Auth.Dtos;

namespace AppForge.Api.Features.Auth.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<LoginRequest>.")]
internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        // BCrypt silently truncates input to 72 bytes. Cap the validator at 72 so users
        // see a validation error rather than silent truncation of long passphrases.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(72);
    }
}
