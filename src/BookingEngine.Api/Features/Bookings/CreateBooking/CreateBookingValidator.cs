using FluentValidation;

namespace BookingEngine.Api.Features.Bookings.CreateBooking;

public sealed class CreateBookingValidator : AbstractValidator<CreateBookingCommand>
{
    public CreateBookingValidator()
    {
        RuleFor(x => x.ResourceId)
            .NotEmpty().WithMessage("ResourceId is required.");

        RuleFor(x => x.CustomerEmail)
            .NotEmpty().WithMessage("CustomerEmail is required.")
            .EmailAddress().WithMessage("CustomerEmail must be a valid email address.")
            .MaximumLength(255);

        RuleFor(x => x.IdempotencyKey)
            .NotEmpty().WithMessage("The 'Idempotency-Key' header is required.")
            .MaximumLength(128);

        RuleFor(x => x.StartUtc)
            .Must(dt => dt.Kind == DateTimeKind.Utc || dt.Kind == DateTimeKind.Unspecified)
            .WithMessage("StartUtc must be in UTC.");

        RuleFor(x => x.EndUtc)
            .Must(dt => dt.Kind == DateTimeKind.Utc || dt.Kind == DateTimeKind.Unspecified)
            .WithMessage("EndUtc must be in UTC.")
            .GreaterThan(x => x.StartUtc)
            .WithMessage("EndUtc must be after StartUtc.");
    }
}