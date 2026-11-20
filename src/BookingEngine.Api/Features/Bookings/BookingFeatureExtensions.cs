using BookingEngine.Api.Features.Bookings.CreateBooking;

namespace BookingEngine.Api.Features.Bookings;

public static class BookingFeatureExtensions
{
    public static IServiceCollection AddBookingFeatures(this IServiceCollection services)
    {
        services.AddScoped<CreateBookingHandler>();
        return services;
    }

    public static RouteGroupBuilder MapBookingEndpoints(this RouteGroupBuilder apiGroup)
    {
        var bookingsGroup = apiGroup.MapGroup("/bookings")
            .WithTags("Bookings");

        bookingsGroup.MapCreateBooking();

        return apiGroup;
    }
}
