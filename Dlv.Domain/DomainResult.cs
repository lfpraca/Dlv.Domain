using System.ComponentModel;

namespace Dlv.Domain;

public interface DomainResult<T>
{
    public record Success(T Value) : DomainResult<T>;
    public record Failure(List<DomainError> Errors) : DomainResult<T>;

    public T Raise()
    {
        return this switch
        {
            Success success => success.Value,
            Failure failure => throw new DomainException(failure.Errors),
            _ => throw new InvalidEnumArgumentException()
        };
    }
}
