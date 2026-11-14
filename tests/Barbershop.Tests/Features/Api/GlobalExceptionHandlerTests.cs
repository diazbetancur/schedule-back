using Api.Barbershop.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Barbershop.Tests.Features.Api;

/// <summary>BUG-072, BUG-039 and BUG-021: how the central handler answers database and domain errors.</summary>
public sealed class GlobalExceptionHandlerTests
{
  [Theory]
  [InlineData("23505", GlobalExceptionHandler.DuplicateMessage)]
  [InlineData("23503", GlobalExceptionHandler.ConcurrentChangeMessage)]
  [InlineData("40001", GlobalExceptionHandler.ConcurrentChangeMessage)]
  [InlineData("40P01", GlobalExceptionHandler.ConcurrentChangeMessage)]
  public async Task ADatabaseConflict_Answers409InSpanish(string sqlState, string expectedDetail)
  {
    var exception = new DbUpdateException("save failed", new PostgresException("duplicate key", "ERROR", "ERROR", sqlState));

    var (status, problem) = await HandleAsync(exception);

    Assert.Equal(StatusCodes.Status409Conflict, status);
    Assert.Equal(expectedDetail, problem.Detail);
    Assert.Equal("conflict", problem.Extensions["code"]);
  }

  [Fact]
  public async Task AConcurrencyException_Answers409()
  {
    var (status, problem) = await HandleAsync(new DbUpdateConcurrencyException("expected 1 row, got 0"));

    Assert.Equal(StatusCodes.Status409Conflict, status);
    Assert.Equal(GlobalExceptionHandler.ConcurrentChangeMessage, problem.Detail);
  }

  [Fact]
  public async Task ANulCharacterRejectedByPostgres_Answers422()
  {
    var exception = new DbUpdateException("save failed", new PostgresException("invalid byte sequence", "ERROR", "ERROR", "22021"));

    var (status, problem) = await HandleAsync(exception);

    Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
    Assert.Equal(InputValueGuard.NulMessage, problem.Detail);
  }

  [Fact]
  public async Task ADomainArgumentException_AnswersAGenericSpanishMessage()
  {
    var (status, problem) = await HandleAsync(
        new ArgumentException("The value must be 2000 characters or fewer.", "value"));

    Assert.Equal(StatusCodes.Status400BadRequest, status);
    Assert.Equal(GlobalExceptionHandler.InvalidDataMessage, problem.Detail);
  }

  private static async Task<(int Status, ProblemDetails Problem)> HandleAsync(Exception exception)
  {
    var problemDetails = new CapturingProblemDetailsService();
    var handler = new GlobalExceptionHandler(problemDetails, new TestEnvironment(), NullLogger<GlobalExceptionHandler>.Instance);
    var context = new DefaultHttpContext();

    Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
    return (context.Response.StatusCode, problemDetails.Written!);
  }

  private sealed class CapturingProblemDetailsService : IProblemDetailsService
  {
    public ProblemDetails? Written { get; private set; }

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
      Written = context.ProblemDetails;
      return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
    {
      Written = context.ProblemDetails;
      return ValueTask.FromResult(true);
    }
  }

  private sealed class TestEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
  }
}
