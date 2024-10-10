namespace Dlv.Domain.Annotations;

[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
#pragma warning disable CS9113 // Parameter is unread.
public class DlvDomainRenameAttribute(string? key) : Attribute;
#pragma warning restore CS9113 // Parameter is unread.
