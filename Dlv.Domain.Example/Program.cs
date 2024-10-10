using Dlv.Domain.Annotations;

namespace Dlv.Domain.Example;

public class Program {
    static void Main(string[] args) {
        var myDomain = MyDomain.TryNew(
            42,
            43,
            44,
            45,
            NonZeroInt.TryNew(50)).Raise();
        try {
            myDomain.SetMyNz(NonZeroInt.TryNew(5));
            Console.WriteLine(myDomain.MyInt);
        } catch (DomainException ex) {
            Console.WriteLine(ex.Errors.FirstOrDefault()!.Error);
        }
    }
}

[DlvDomain]
public partial class MyDomain {
    [DlvDomainValidation]
    private static IReadOnlyList<DomainError>? MyInt_ShouldBeSmallerThan_MyNz(int MyInt, NonZeroInt MyNz) {
        if (MyInt >= MyNz.Value) {
            return [new("MyInt", "MyInt should be smaller than MyNz")];
        }
        return null;
    }
    [DlvDomainValidation]
    private static IEnumerable<DomainError>? MyInt4_ShouldBeSmallerThan_MyNz(int MyInt4, NonZeroInt MyNz) {
        if (MyInt4 >= MyNz.Value) {
            yield return new("MyInt4", "MyInt4 should be smaller than MyNz");
        }
    }
    [DlvDomainValidation]
    private static string? MyInt4_ShouldNotBeEqualTo_MyInt(int MyInt4, int MyInt) {
        if (MyInt4 == MyInt) {
            return "MyInt4 should not be equal to MyInt";
        }

        return null;
    }
    public int MyInt { get; private set; }
    public int MyInt2 { get; private set; }
    public int MyInt3 { get; private init; }
    public int MyInt4 { get; private set; }
    public NonZeroInt MyNz { get; private set; }
}

[DlvDomain]
public partial class NonZeroInt {
    [DlvDomainValidation]
    private static IEnumerable<DomainError>? Value_ShouldNotBeZero(int Value) {
        if (Value == 0) {
            return [new(null, "Value is zero")];
        }
        return null;
    }
    public int Value { get; private init; }
}
