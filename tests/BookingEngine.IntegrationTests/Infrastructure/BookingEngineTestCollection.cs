using Xunit;

namespace BookingEngine.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class BookingEngineTestCollection : ICollectionFixture<BookingEngineWebAppFactory>
{
    public const string Name = "BookingEngineCollection";
}
