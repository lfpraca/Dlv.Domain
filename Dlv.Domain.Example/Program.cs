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
    private static IEnumerable<string>? MyInt_ShouldBeSmallerThan_MyNz(int MyInt, NonZeroInt MyNz) {
        if (MyInt >= MyNz.Value) {
            yield return "MyInt should be smaller than MyNz";
        }
    }
    [DlvDomainValidation]
    private static IEnumerable<DomainError>? MyInt4_ShouldBeSmallerThan_MyNz(int MyInt4, NonZeroInt MyNz) {
        if (MyInt4 >= MyNz.Value) {
            yield return new("MyInt4", "MyInt4 should be smaller than MyNz");
        }
    }
    [DlvDomainValidation]
    private static IEnumerable<string> MyInt4_ShouldNotBeEqualTo_MyInt(int MyInt4, int MyInt) {
        if (MyInt4 == MyInt) {
            yield return "MyInt4 should not be equal to MyInt";
        }
    }
    public int MyInt { get; private set; }
    public int MyInt2 { get; private set; }
    public int MyInt3 { get; private init; }
    [DlvDomainRename("OtherName")]
    public int MyInt4 { get; private set; }
    public NonZeroInt MyNz { get; private set; }
}

[DlvDomain]
public partial class NonZeroInt {
    [DlvDomainValidation]
    private static IEnumerable<string> Value_ShouldNotBeZero(int Value) {
        if (Value == 0) {
            yield return "Value is zero";
        }
    }
    [DlvDomainRename(null)]
    public int Value { get; private init; }
}
