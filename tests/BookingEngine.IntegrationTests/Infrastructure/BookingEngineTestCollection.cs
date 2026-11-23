using Xunit;

namespace BookingEngine.IntegrationTests.Infrastructure;

[CollectionDefinition("BookingEngineCollection")]
public sealed class BookingEngineTestCollection : ICollectionFixture<BookingEngineWebAppFactory>
{
    public const string Name = "BookingEngineCollection";
}
