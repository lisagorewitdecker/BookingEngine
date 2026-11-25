using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BookingEngine.IntegrationTests;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ReplacesNewlinesInLoggedPathAndDomainExceptionMessage()
    {
        var logger = new CapturingLogger<GlobalExceptionHandler>();
        using var serviceProvider = CreateServiceProvider();
        var handler = CreateHandler(serviceProvider, logger);
        var httpContext = CreateHttpContext("GET\r\nforged", "/safe\r\nforged");

        await handler.TryHandleAsync(
            httpContext,
            new DomainException("test.error", "user input\r\nforged log entry"),
            CancellationToken.None);

        var loggedMessage = Assert.Single(logger.Messages);
        Assert.DoesNotContain('\r', loggedMessage);
        Assert.DoesNotContain('\n', loggedMessage);
        Assert.Contains("GET  forged", loggedMessage);
        Assert.Contains("/safe  forged", loggedMessage);
        Assert.Contains("user input  forged log entry", loggedMessage);
    }

    [Fact]
    public async Task TryHandleAsync_ReplacesNewlinesInPathLoggedForUnexpectedException()
    {
        var logger = new CapturingLogger<GlobalExceptionHandler>();
        using var serviceProvider = CreateServiceProvider();
        var handler = CreateHandler(serviceProvider, logger);
        var httpContext = CreateHttpContext("GET\r\nforged", "/safe\r\nforged");

        await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("unexpected\r\nforged exception"),
            CancellationToken.None);

        var loggedMessage = Assert.Single(logger.Messages);
        Assert.DoesNotContain('\r', loggedMessage);
        Assert.DoesNotContain('\n', loggedMessage);
        Assert.Contains("GET  forged", loggedMessage);
        Assert.Contains("/safe  forged", loggedMessage);
        Assert.Contains("unexpected  forged exception", loggedMessage);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddProblemDetails();
        return services.BuildServiceProvider();
    }

    private static GlobalExceptionHandler CreateHandler(
        IServiceProvider serviceProvider,
        ILogger<GlobalExceptionHandler> logger) =>
        new(serviceProvider.GetRequiredService<IProblemDetailsService>(), logger);

    private static DefaultHttpContext CreateHttpContext(string method, string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
