namespace Foreign;

/// <summary>
/// Stands in for <c>Newtonsoft.Json.JsonConstructorAttribute</c>. The production matcher keys on the
/// attribute's NAME, so this proves the mechanism without the test project taking a dependency on
/// Newtonsoft in order to assert a behaviour that is about not depending on it.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class JsonConstructorAttribute : Attribute;
