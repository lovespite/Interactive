namespace Interactive.Attributes;

[AttributeUsage(AttributeTargets.Parameter)]
public class InteractiveParameterAttribute : Attribute
{
    public string? Description { get; set; }
}
