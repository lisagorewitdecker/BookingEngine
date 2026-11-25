using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BookingEngine.IntegrationTests.Common.Exceptions;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ReplacesLineBreaksInWarningMessage()
    {
        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(new NoOpProblemDetailsService(), logger);
        var context = new DefaultHttpContext();
        var exception = new DomainException("request.invalid", "Invalid input\r\nforged log entry");

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.Single(logger.Warnings);
        Assert.Contains("Invalid input  forged log entry", logger.Warnings[0]);
        Assert.DoesNotContain('\r', logger.Warnings[0]);
        Assert.DoesNotContain('\n', logger.Warnings[0]);
    public async Task TryHandleAsync_WhenDomainExceptionMessageContainsLineBreaks_LogsSingleLine()
    {
        var logger = new CapturingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(new NoOpProblemDetailsService(), logger);

        await handler.TryHandleAsync(
            new DefaultHttpContext(),
            new DomainException("booking.test", "first line\r\nforged entry"),
            CancellationToken.None);

        string logMessage = Assert.Single(logger.Messages);
        Assert.DoesNotContain('\r', logMessage);
        Assert.DoesNotContain('\n', logMessage);
        Assert.Contains("first line forged entry", logMessage);
    }

    private sealed class NoOpProblemDetailsService : IProblemDetailsService
    {
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<string> Warnings { get; } = [];
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(false);
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
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
            Messages.Add(formatter(state, exception));
        }
    }
}
