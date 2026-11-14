using OrderFlow.Shared;
using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Api;

public static class SubmissionHttpMapping
{
    public static IResult ToHttpResult(this Result<SubmitOrderOutput> result)
    {
        if (result.IsSuccess)
        {
            return Results.Json(
                result.Value.ToResponse(),
                statusCode: result.Value.Status == "PendingConfirmation" ? 202 : 200);
        }

        var error = result.Error;
        var status = error.Code switch
        {
            "ValidationFailed" => 400,
            "IdentityConflict" => 409,
            "FixUnavailable" or "RegistryCapacityReached" => 503,
            "OrderNotFound" => 404,
            _ => throw new InvalidOperationException($"Unmapped application error: {error.Code}")
        };

        return status == 404
            ? Results.NotFound()
            : Results.Json(
                new OrderError(error.Message, error.Code, error.Fields),
                statusCode: status);
    }
}
