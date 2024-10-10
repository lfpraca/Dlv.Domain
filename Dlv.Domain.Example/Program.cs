using Dlv.Domain.Annotations;
using System.Collections.ObjectModel;

namespace Dlv.Domain.Example;

public class Program {
    static void Main(string[] args) {
        List<int> list = [50, 51];
        var myDomain = MyDomain.TryNew(
            42,
            43,
            44,
            45,
            NonEmptyList.TryNew(list.Select(static x => NonZeroInt.TryNew(x)))).Raise();
        try {
            myDomain.SetMyNz(NonEmptyList.TryNew([NonZeroInt.TryNew(5)]));
            Console.WriteLine(myDomain.MyNz.Value.First().Value);
        } catch (DomainException ex) {
            Console.WriteLine(ex.Errors.FirstOrDefault()!.Error);
        }
    }
}

[DlvDomain]
public partial class MyDomain {
    //[DlvDomainValidation]
    //private static IEnumerable<string>? MyInt_ShouldBeSmallerThan_MyNz(int MyInt, List<NonZeroInt> MyNz) {
    //    yield return "MyInt should be smaller than MyNz";
    //}
    //[DlvDomainValidation]
    //private static IEnumerable<DomainError>? MyInt4_ShouldBeSmallerThan_MyNz(int MyInt4, List<NonZeroInt> MyNz) {
    //    yield return new("MyInt4", "MyInt4 should be smaller than MyNz");
    //}
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
    public NonEmptyList MyNz { get; private set; }
}

[DlvDomain]
public partial class NonEmptyList {
    [DlvDomainValidation]
    private static IEnumerable<string> List_ShouldNotBeEmpty(List<NonZeroInt> _value) {
        if (_value.Count == 0) {
            yield return "List is empty";
        }
    }

    [DlvDomainRename(null)]
    private List<NonZeroInt> _value { get; init; }

    public ReadOnlyCollection<NonZeroInt> Value => this._value.AsReadOnly();
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
