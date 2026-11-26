#if NETSTANDARD2_0
// Enables C# 9 init-only setters / records on netstandard2.0 (the compiler only needs the type
// to exist; it ships in the BCL from .NET 5 onward).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}

// The nullable flow analysis of a Try pattern (an override must repeat the attribute of the member it overrides).
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
    {
        public bool ReturnValue { get; } = returnValue;
    }
}
#endif
