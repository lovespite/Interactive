namespace Interactive.Attributes;

[AttributeUsage(AttributeTargets.Parameter)]
public class InteractiveParameterAttribute : Attribute
{
    /// <summary>
    /// 参数名称
    /// </summary>
    public string? Alias { get; set; }
    /// <summary>
    /// 指定参数是否可以继承自上一个命令的输出
    /// </summary>
    public bool PipeIn { get; set; }
    /// <summary>
    /// 参数描述
    /// </summary>
    public string? Description { get; set; }
    /// <summary>
    /// 默认值的显示文本
    /// </summary>
    public string? DefaultValueDisplayText { get; set; }
}
