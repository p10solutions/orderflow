using FluentValidation;
using FluentValidation.Results;

namespace OrderFlow.Shared.Validation;

public static class InputValidation
{
    public static Error? ValidateInput<T>(
        this IValidator<T> validator,
        T input,
        string code = "ValidationFailed")
    {
        var validation = input is null
            ? new ValidationResult([new ValidationFailure("input", "Input is required")])
            : validator.Validate(
                input);

        if (validation.IsValid)
        {
            return null;
        }

        var fields = validation.Errors.GroupBy(error => error.PropertyName).ToDictionary(
            group => group.Key,
            group => group.First().ErrorMessage);

        return new(code, validation.Errors[0].ErrorMessage, fields);
    }
}
