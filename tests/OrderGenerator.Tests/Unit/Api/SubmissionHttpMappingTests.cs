using OrderFlow.Shared;
using Microsoft.AspNetCore.Http;
using OrderGenerator;
using OrderGenerator.Api;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class SubmissionHttpMappingTests
{
    [Theory]
    [InlineData("ValidationFailed", 400)]
    [InlineData("IdentityConflict", 409)]
    [InlineData("FixUnavailable", 503)]
    [InlineData("RegistryCapacityReached", 503)]
    [InlineData("OrderNotFound", 404)]
    public void Errors_are_translated_by_api(string code, int expected) => Assert.Equal(
        expected,
        ((IStatusCodeHttpResult)Result<SubmitOrderOutput>.Failure(
            new(
                code,
                "reason")).ToHttpResult()).StatusCode);

    [Theory]
    [InlineData("PendingConfirmation", 202)]
    [InlineData("Accepted", 200)]
    [InlineData("Rejected", 200)]
    public void Decisions_and_pending_preserve_http_contract(string status, int expected) => Assert.Equal(
        expected,
        ((IStatusCodeHttpResult)Result<SubmitOrderOutput>.Success(
            new(
                "id",
                status)).ToHttpResult()).StatusCode);
}
