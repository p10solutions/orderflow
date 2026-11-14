namespace OrderGenerator.Api.Middleware;

internal sealed class SameOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (IsOrderSubmission(context.Request) && !HasAllowedOrigin(context.Request))
        {
            return HttpErrorResponses.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                new(
                    "Origin is not allowed",
                    "OriginNotAllowed"));
        }

        return next(context);
    }

    private static bool IsOrderSubmission(HttpRequest request) => HttpMethods.IsPost(request.Method) && string.Equals(
        request.Path.Value?.TrimEnd('/'),
        OrderHttpPolicy.SubmissionPath,
        StringComparison.OrdinalIgnoreCase);

    private static bool HasAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin;

        if (origin.Count == 0)
        {
            return true;
        }

        return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var source) && source.GetLeftPart(
            UriPartial.Authority) == origin[0] && string.Equals(
            source.GetLeftPart(
                UriPartial.Authority),
            $"{request.Scheme}://{request.Host}",
            StringComparison.OrdinalIgnoreCase);
    }
}
