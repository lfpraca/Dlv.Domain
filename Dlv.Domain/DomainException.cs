namespace Dlv.Domain;

public class DomainException : Exception
{
    public List<DomainError> Errors { get; private init; }
    public DomainException(List<DomainError> errors) : base($"{errors.Count} validation exception{(errors.Count == 1 ? null : 's')} occurred, see Errors for details")
    {
        Errors = errors;
    }
}
